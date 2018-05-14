﻿using Microsoft.Graph;
using Microsoft.SharePoint.Administration;
using Microsoft.SharePoint.Administration.Claims;
using Microsoft.SharePoint.Utilities;
using Microsoft.SharePoint.WebControls;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using static azurecp.ClaimsProviderLogging;
using WIF4_5 = System.Security.Claims;

/*
 * DO NOT directly edit AzureCP class. It is designed to be inherited to customize it as desired.
 * Please download "AzureCP for Developers.zip" on https://github.com/Yvand/AzureCP to find examples and guidance.
 * */

namespace azurecp
{
    /// <summary>
    /// Provides search and resolution against Azure Active Directory
    /// Visit https://github.com/Yvand/AzureCP for documentation and updates.
    /// Please report any bug to https://github.com/Yvand/AzureCP.
    /// Author: Yvan Duhamel
    /// </summary>
    public class AzureCP : SPClaimProvider
    {
        public const string _ProviderInternalName = "AzureCP";
        public virtual string ProviderInternalName => "AzureCP";
        public virtual string PersistedObjectName => ClaimsProviderConstants.AZURECPCONFIG_NAME;

        private object Lock_Init = new object();
        private ReaderWriterLockSlim Lock_Config = new ReaderWriterLockSlim();
        private long CurrentConfigurationVersion = 0;

        /// <summary>
        /// Contains configuration currently used by claims provider
        /// </summary>
        public IAzureCPConfiguration CurrentConfiguration;

        /// <summary>
        /// SPTrust associated with the claims provider
        /// </summary>
        protected SPTrustedLoginProvider SPTrust;

        /// <summary>
        /// ClaimTypeConfig mapped to the identity claim in the SPTrustedIdentityTokenIssuer
        /// </summary>
        ClaimTypeConfig IdentityClaimTypeConfig;

        /// <summary>
        /// Group ClaimTypeConfig used to set the claim type for other group ClaimTypeConfig that have UseMainClaimTypeOfDirectoryObject set to true
        /// </summary>
        ClaimTypeConfig MainGroupClaimTypeConfig;

        /// <summary>
        /// Processed list to use. It is guarranted to never contain an empty ClaimType
        /// </summary>
        public List<ClaimTypeConfig> ProcessedClaimTypesList;
        protected IEnumerable<ClaimTypeConfig> MetadataConfig;
        protected virtual string PickerEntityDisplayText { get { return "({0}) {1}"; } }
        protected virtual string PickerEntityOnMouseOver { get { return "{0}={1}"; } }

        protected string IssuerName
        {
            get
            {
                // The advantage of using the SPTrustedLoginProvider name for the issuer name is that it makes possible and easy to replace current claims provider with another one.
                // The other claims provider would simply have to use SPTrustedLoginProvider name too
                return SPOriginalIssuers.Format(SPOriginalIssuerType.TrustedProvider, SPTrust.Name);
            }
        }

        public AzureCP(string displayName) : base(displayName)
        {
        }

        /// <summary>
        /// Initializes claim provider. This method is reserved for internal use and is not intended to be called from external code or changed
        /// </summary>
        public bool Initialize(Uri context, string[] entityTypes)
        {
            // Ensures thread safety to initialize class variables
            lock (Lock_Init)
            {
                // 1ST PART: GET CONFIGURATION OBJECT
                IAzureCPConfiguration globalConfiguration = null;
                bool refreshConfig = false;
                bool success = true;
                try
                {
                    if (SPTrust == null)
                    {
                        SPTrust = GetSPTrustAssociatedWithCP(ProviderInternalName);
                        if (SPTrust == null) return false;
                    }
                    if (!CheckIfShouldProcessInput(context)) return false;

                    globalConfiguration = GetConfiguration(context, entityTypes, PersistedObjectName);
                    if (globalConfiguration == null)
                    {
                        ClaimsProviderLogging.Log($"[{ProviderInternalName}] Configuration '{PersistedObjectName}' was not found. Visit AzureCP admin pages in central administration to create it.",
                            TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);
                        // Create a fake persisted object just to get the default settings, it will not be saved in config database
                        globalConfiguration = AzureCPConfig.GetDefaultConfiguration();
                        refreshConfig = true;
                    }
                    else if (globalConfiguration.ClaimTypes == null || globalConfiguration.ClaimTypes.Count == 0)
                    {
                        ClaimsProviderLogging.Log($"[{ProviderInternalName}] Configuration '{PersistedObjectName}' was found but collection ClaimTypes is null or empty. Visit AzureCP admin pages in central administration to create it.",
                            TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);
                        // Cannot continue 
                        success = false;
                    }
                    else if (globalConfiguration.AzureTenants == null || globalConfiguration.AzureTenants.Count == 0)
                    {
                        ClaimsProviderLogging.Log($"[{ProviderInternalName}] Configuration '{PersistedObjectName}' was found but there is no Azure AD tenant registered. Visit AzureCP admin pages in central administration to register it.",
                            TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);
                        // Cannot continue 
                        success = false;
                    }
                    else
                    {
                        // Persisted object is found
                        if (this.CurrentConfigurationVersion == ((SPPersistedObject)globalConfiguration).Version)
                        {
                            ClaimsProviderLogging.Log($"[{ProviderInternalName}] Configuration '{PersistedObjectName}' was found, version {((SPPersistedObject)globalConfiguration).Version.ToString()}",
                                TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Core);
                        }
                        else
                        {
                            refreshConfig = true;
                            this.CurrentConfigurationVersion = ((SPPersistedObject)globalConfiguration).Version;
                            ClaimsProviderLogging.Log($"[{ProviderInternalName}] Configuration '{PersistedObjectName}' changed to version {((SPPersistedObject)globalConfiguration).Version.ToString()}, refreshing local copy",
                                TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Core);
                        }
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    ClaimsProviderLogging.LogException(ProviderInternalName, "in Initialize", TraceCategory.Core, ex);
                }
                finally
                { }

                if (!success) return success;
                if (!refreshConfig) return success;

                // 2ND PART: APPLY CONFIGURATION
                // Configuration needs to be refreshed, lock current thread in write mode
                Lock_Config.EnterWriteLock();
                try
                {
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Refreshing local copy of configuration '{PersistedObjectName}'",
                        TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Core);

                    // Create local persisted object that will never be saved in config DB, it's just a local copy
                    // This copy is unique to current object instance to avoid thread safety issues
                    this.CurrentConfiguration = ((AzureCPConfig)globalConfiguration).CopyCurrentObject();

                    SetCustomConfiguration(context, entityTypes);
                    if (this.CurrentConfiguration.ClaimTypes == null)
                    {
                        // this.CurrentConfiguration.ClaimTypes was set to null in SetCustomConfiguration, which is bad
                        ClaimsProviderLogging.Log(String.Format("[{0}] ClaimTypes was set to null in SetCustomConfiguration, don't set it or set it with actual entries.", ProviderInternalName), TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);
                        return false;
                    }

                    if (this.CurrentConfiguration.AzureTenants == null || this.CurrentConfiguration.AzureTenants.Count == 0)
                    {
                        ClaimsProviderLogging.Log(String.Format("[{0}] AzureTenants was not set. Override method SetCustomConfiguration to set it.", ProviderInternalName), TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);
                        return false;
                    }

                    // Set properties AuthenticationProvider and GraphService
                    foreach (var tenant in this.CurrentConfiguration.AzureTenants)
                    {
                        tenant.SetAzureADContext();
                    }
                    success = this.InitializeClaimTypeConfigList(this.CurrentConfiguration.ClaimTypes);
                }
                catch (Exception ex)
                {
                    success = false;
                    ClaimsProviderLogging.LogException(ProviderInternalName, "in Initialize, while refreshing configuration", TraceCategory.Core, ex);
                }
                finally
                {
                    Lock_Config.ExitWriteLock();
                }
                return success;
            }
        }

        /// <summary>
        /// Initializes claim provider. This method is reserved for internal use and is not intended to be called from external code or changed
        /// </summary>
        /// <param name="nonProcessedClaimTypes"></param>
        /// <returns></returns>
        private bool InitializeClaimTypeConfigList(ClaimTypeConfigCollection nonProcessedClaimTypes)
        {
            bool success = true;
            try
            {
                bool identityClaimTypeFound = false;
                bool groupClaimTypeFound = false;
                // Get claim types defined in SPTrustedLoginProvider based on their claim type (unique way to map them)
                List<ClaimTypeConfig> claimTypesSetInTrust = new List<ClaimTypeConfig>();
                foreach (SPTrustedClaimTypeInformation claimTypeInformation in SPTrust.ClaimTypeInformation)
                {
                    // Search if current claim type in trust exists in AzureADObjects
                    // List<T>.FindAll returns an empty list if no result found: http://msdn.microsoft.com/en-us/library/fh1w7y8z(v=vs.110).aspx
                    ClaimTypeConfig claimTypeConfig = nonProcessedClaimTypes.FirstOrDefault(x =>
                        String.Equals(x.ClaimType, claimTypeInformation.MappedClaimType, StringComparison.InvariantCultureIgnoreCase) &&
                        !x.UseMainClaimTypeOfDirectoryObject &&
                        x.DirectoryObjectProperty != AzureADObjectProperty.NotSet);

                    if (claimTypeConfig == null) continue;
                    claimTypesSetInTrust.Add(claimTypeConfig);
                    if (String.Equals(SPTrust.IdentityClaimTypeInformation.MappedClaimType, claimTypeConfig.ClaimType, StringComparison.InvariantCultureIgnoreCase))
                    {
                        // Identity claim type found, set IdentityAzureADObject property
                        identityClaimTypeFound = true;
                        IdentityClaimTypeConfig = claimTypeConfig;
                    }
                    else if (claimTypeConfig.DirectoryObjectType == AzureADObjectType.Group && !groupClaimTypeFound)
                    {
                        groupClaimTypeFound = true;
                        MainGroupClaimTypeConfig = claimTypeConfig;
                    }
                }

                // Check if identity claim is there. Should always check property SPTrustedClaimTypeInformation.MappedClaimType: http://msdn.microsoft.com/en-us/library/microsoft.sharepoint.administration.claims.sptrustedclaimtypeinformation.mappedclaimtype.aspx
                if (!identityClaimTypeFound)
                {
                    ClaimsProviderLogging.Log(String.Format("[{0}] Impossible to continue because identity claim type '{1}' set in the SPTrustedIdentityTokenIssuer '{2}' is missing in AzureADObjects.", ProviderInternalName, SPTrust.IdentityClaimTypeInformation.MappedClaimType, SPTrust.Name), TraceSeverity.Unexpected, EventSeverity.ErrorCritical, TraceCategory.Core);
                    return false;
                }

                // Check if there are objects that should be always queried (UseMainClaimTypeOfDirectoryObject) to add in the list
                List<ClaimTypeConfig> additionalClaimTypeConfigList = new List<ClaimTypeConfig>();
                foreach (ClaimTypeConfig claimTypeConfig in nonProcessedClaimTypes.Where(x => x.UseMainClaimTypeOfDirectoryObject))
                {
                    if (claimTypeConfig.DirectoryObjectType == AzureADObjectType.User)
                    {
                        claimTypeConfig.ClaimType = IdentityClaimTypeConfig.ClaimType;
                        claimTypeConfig.DirectoryObjectPropertyToShowAsDisplayText = IdentityClaimTypeConfig.DirectoryObjectPropertyToShowAsDisplayText;
                    }
                    else
                    {
                        // If not a user, it must be a group
                        claimTypeConfig.ClaimType = MainGroupClaimTypeConfig.ClaimType;
                        claimTypeConfig.DirectoryObjectPropertyToShowAsDisplayText = MainGroupClaimTypeConfig.DirectoryObjectPropertyToShowAsDisplayText;
                    }
                    additionalClaimTypeConfigList.Add(claimTypeConfig);
                }

                ProcessedClaimTypesList = new List<ClaimTypeConfig>(claimTypesSetInTrust.Count + additionalClaimTypeConfigList.Count);
                ProcessedClaimTypesList.AddRange(claimTypesSetInTrust);
                ProcessedClaimTypesList.AddRange(additionalClaimTypeConfigList);

                // Parse objects to configure some settings
                // An object can have ClaimType set to null if only used to populate metadata of permission created
                foreach (var attr in ProcessedClaimTypesList.Where(x => x.ClaimType != null))
                {
                    var trustedClaim = SPTrust.GetClaimTypeInformationFromMappedClaimType(attr.ClaimType);
                    // It should never be null
                    if (trustedClaim == null) continue;
                    attr.ClaimTypeDisplayName = trustedClaim.DisplayName;
                }

                // Get all PickerEntity metadata with a DirectoryObjectProperty set
                this.MetadataConfig = nonProcessedClaimTypes.Where(x =>
                    !String.IsNullOrEmpty(x.EntityDataKey) &&
                    x.DirectoryObjectProperty != AzureADObjectProperty.NotSet);
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in InitializeClaimTypeConfigList", TraceCategory.Core, ex);
                success = false;
            }
            return success;
        }

        /// <summary>
        /// DO NOT Override this method if you use a custom persisted object to hold your configuration.
        /// To get you custom persisted object, you must override property LDAPCP.PersistedObjectName and set its name
        /// </summary>
        /// <returns></returns>
        protected virtual IAzureCPConfiguration GetConfiguration(Uri context, string[] entityTypes, string persistedObjectName)
        {
            return AzureCPConfig.GetConfiguration(persistedObjectName);
        }

        /// <summary>
        /// Override this method to customize configuration of AzureCP
        /// </summary> 
        /// <param name="context">The context, as a URI</param>
        /// <param name="entityTypes">The EntityType entity types set to scope the search to</param>
        protected virtual void SetCustomConfiguration(Uri context, string[] entityTypes)
        {
        }

        /// <summary>
        /// Check if AzureCP should process input (and show results) based on current URL (context)
        /// </summary>
        /// <param name="context">The context, as a URI</param>
        /// <returns></returns>
        protected virtual bool CheckIfShouldProcessInput(Uri context)
        {
            if (context == null) return true;
            var webApp = SPWebApplication.Lookup(context);
            if (webApp == null) return false;
            if (webApp.IsAdministrationWebApplication) return true;

            // Not central admin web app, enable AzureCP only if current web app uses it
            // It is not possible to exclude zones where AzureCP is not used because:
            // Consider following scenario: default zone is WinClaims, intranet zone is Federated:
            // In intranet zone, when creating permission, AzureCP will be called 2 times. The 2nd time (in FillResolve (SPClaim)), the context will always be the URL of the default zone
            foreach (var zone in Enum.GetValues(typeof(SPUrlZone)))
            {
                SPIisSettings iisSettings = webApp.GetIisSettingsWithFallback((SPUrlZone)zone);
                if (!iisSettings.UseTrustedClaimsAuthenticationProvider)
                    continue;

                // Get the list of authentication providers associated with the zone
                foreach (SPAuthenticationProvider prov in iisSettings.ClaimsAuthenticationProviders)
                {
                    if (prov.GetType() == typeof(Microsoft.SharePoint.Administration.SPTrustedAuthenticationProvider))
                    {
                        // Check if the current SPTrustedAuthenticationProvider is associated with the claim provider
                        if (String.Equals(prov.ClaimProviderName, ProviderInternalName, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Get the first TrustedLoginProvider associated with current claim provider
        /// LIMITATION: The same claims provider (uniquely identified by its name) cannot be associated to multiple TrustedLoginProvider because at runtime there is no way to determine what TrustedLoginProvider is currently calling
        /// </summary>
        /// <param name="providerInternalName"></param>
        /// <returns></returns>
        public static SPTrustedLoginProvider GetSPTrustAssociatedWithCP(string providerInternalName)
        {
            var lp = SPSecurityTokenServiceManager.Local.TrustedLoginProviders.Where(x => String.Equals(x.ClaimProviderName, providerInternalName, StringComparison.OrdinalIgnoreCase));

            if (lp != null && lp.Count() == 1)
                return lp.First();

            if (lp != null && lp.Count() > 1)
                ClaimsProviderLogging.Log(String.Format("[{0}] Claims provider {0} is associated to multiple SPTrustedIdentityTokenIssuer, which is not supported because at runtime there is no way to determine what TrustedLoginProvider is currently calling", providerInternalName), TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);

            ClaimsProviderLogging.Log(String.Format("[{0}] Claims provider {0} is not associated with any SPTrustedIdentityTokenIssuer so it cannot create permissions.\r\nVisit http://ldapcp.codeplex.com for installation procedure or set property ClaimProviderName with PowerShell cmdlet Get-SPTrustedIdentityTokenIssuer to create association.", providerInternalName), TraceSeverity.High, EventSeverity.Warning, TraceCategory.Core);
            return null;
        }

        /// <summary>
        /// Uses reflection to return the value of a public property for the given object
        /// </summary>
        /// <param name="directoryObject"></param>
        /// <param name="propertyName"></param>
        /// <returns>Null if property doesn't exist, String.Empty if property exists but has no value, actual value otherwise</returns>
        public static string GetPropertyValue(object directoryObject, string propertyName)
        {
            PropertyInfo pi = directoryObject.GetType().GetProperty(propertyName);
            if (pi == null) return null;    // Property doesn't exist
            object propertyValue = pi.GetValue(directoryObject, null);
            return propertyValue == null ? String.Empty : propertyValue.ToString();
        }

        /// <summary>
        /// Create a SPClaim with property OriginalIssuer correctly set
        /// </summary>
        /// <param name="type">Claim type</param>
        /// <param name="value">Claim value</param>
        /// <param name="valueType">Claim value type</param>
        /// <returns>SPClaim object</returns>
        protected virtual new SPClaim CreateClaim(string type, string value, string valueType)
        {
            // SPClaimProvider.CreateClaim sets property OriginalIssuer to SPOriginalIssuerType.ClaimProvider, which is not correct
            //return CreateClaim(type, value, valueType);
            return new SPClaim(type, value, valueType, IssuerName);
        }

        protected virtual PickerEntity CreatePickerEntityHelper(AzureCPResult result)
        {
            PickerEntity pe = CreatePickerEntity();
            SPClaim claim;
            string permissionValue = result.PermissionValue;
            string permissionClaimType = result.ClaimTypeConfig.ClaimType;
            bool isMappedClaimTypeConfig = false;

            if (String.Equals(result.ClaimTypeConfig.ClaimType, IdentityClaimTypeConfig.ClaimType, StringComparison.InvariantCultureIgnoreCase)
                || result.ClaimTypeConfig.UseMainClaimTypeOfDirectoryObject)
            {
                isMappedClaimTypeConfig = true;
            }

            if (result.ClaimTypeConfig.UseMainClaimTypeOfDirectoryObject)
            {
                string claimValueType;
                if (result.ClaimTypeConfig.DirectoryObjectType == AzureADObjectType.User)
                {
                    permissionClaimType = IdentityClaimTypeConfig.ClaimType;
                    pe.EntityType = SPClaimEntityTypes.User;
                    claimValueType = IdentityClaimTypeConfig.ClaimValueType;
                }
                else
                {
                    permissionClaimType = MainGroupClaimTypeConfig.ClaimType;
                    pe.EntityType = ClaimsProviderConstants.GroupClaimEntityType;
                    claimValueType = MainGroupClaimTypeConfig.ClaimValueType;
                }
                permissionValue = FormatPermissionValue(permissionClaimType, permissionValue, isMappedClaimTypeConfig, result);
                claim = CreateClaim(
                    permissionClaimType,
                    permissionValue,
                    claimValueType);
            }
            else
            {
                permissionValue = FormatPermissionValue(permissionClaimType, permissionValue, isMappedClaimTypeConfig, result);
                claim = CreateClaim(
                    permissionClaimType,
                    permissionValue,
                    result.ClaimTypeConfig.ClaimValueType);
                pe.EntityType = result.ClaimTypeConfig.DirectoryObjectType == AzureADObjectType.User ? SPClaimEntityTypes.User : ClaimsProviderConstants.GroupClaimEntityType;
            }

            pe.DisplayText = FormatPermissionDisplayText(permissionClaimType, permissionValue, isMappedClaimTypeConfig, result);
            pe.Description = String.Format(
                PickerEntityOnMouseOver,
                result.ClaimTypeConfig.DirectoryObjectProperty.ToString(),
                result.QueryMatchValue);
            pe.Claim = claim;
            pe.IsResolved = true;
            //pe.EntityGroupName = "";

            int nbMetadata = 0;
            // Populate metadata of new PickerEntity
            foreach (var ctConfig in MetadataConfig.Where(x => x.DirectoryObjectType == result.ClaimTypeConfig.DirectoryObjectType))
            {
                // if there is actally a value in the GraphObject, then it can be set
                string entityAttribValue = GetPropertyValue(result.UserOrGroupResult, ctConfig.DirectoryObjectProperty.ToString());
                if (!String.IsNullOrEmpty(entityAttribValue))
                {
                    pe.EntityData[ctConfig.EntityDataKey] = entityAttribValue;
                    nbMetadata++;
                    ClaimsProviderLogging.Log(String.Format("[{0}] Added metadata '{1}' with value '{2}' to new entity", ProviderInternalName, ctConfig.EntityDataKey, entityAttribValue), TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
                }
            }
            ClaimsProviderLogging.Log($"[{ProviderInternalName}] Created entity: display text: '{pe.DisplayText}', value: '{pe.Claim.Value}', claim type: '{pe.Claim.ClaimType}', and filled with {nbMetadata.ToString()} metadata.", TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
            return pe;
        }

        /// <summary>
        /// Override this method to customize value of permission created
        /// </summary>
        /// <param name="claimType"></param>
        /// <param name="claimValue"></param>
        /// <param name="isIdentityClaimType"></param>
        /// <param name="result"></param>
        /// <returns></returns>
        protected virtual string FormatPermissionValue(string claimType, string claimValue, bool isIdentityClaimType, AzureCPResult result)
        {
            return claimValue;
        }

        /// <summary>
        /// Override this method to customize display text of permission created
        /// </summary>
        /// <param name="displayText"></param>
        /// <param name="claimType"></param>
        /// <param name="claimValue"></param>
        /// <param name="isIdentityClaim"></param>
        /// <param name="result"></param>
        /// <returns></returns>
        protected virtual string FormatPermissionDisplayText(string claimType, string claimValue, bool isIdentityClaimType, AzureCPResult result)
        {
            string permissionDisplayText = String.Empty;
            string valueDisplayedInPermission = String.Empty;

            if (result.ClaimTypeConfig.DirectoryObjectPropertyToShowAsDisplayText != AzureADObjectProperty.NotSet)
            {
                if (!isIdentityClaimType) permissionDisplayText = "(" + result.ClaimTypeConfig.ClaimTypeDisplayName + ") ";

                string graphPropertyToDisplayValue = GetPropertyValue(result.UserOrGroupResult, result.ClaimTypeConfig.DirectoryObjectPropertyToShowAsDisplayText.ToString());
                if (!String.IsNullOrEmpty(graphPropertyToDisplayValue)) permissionDisplayText += graphPropertyToDisplayValue;
                else permissionDisplayText += result.PermissionValue;
            }
            else
            {
                if (isIdentityClaimType)
                {
                    permissionDisplayText = result.QueryMatchValue;
                }
                else
                {
                    permissionDisplayText = String.Format(
                        PickerEntityDisplayText,
                        result.ClaimTypeConfig.ClaimTypeDisplayName,
                        result.PermissionValue);
                }
            }

            return permissionDisplayText;
        }

        protected virtual PickerEntity CreatePickerEntityForSpecificClaimType(string input, ClaimTypeConfig claimTypesToResolve, bool inputHasKeyword)
        {
            List<PickerEntity> entities = CreatePickerEntityForSpecificClaimTypes(
                input,
                new List<ClaimTypeConfig>()
                    {
                        claimTypesToResolve,
                    },
                inputHasKeyword);
            return entities == null ? null : entities.First();
        }

        protected virtual List<PickerEntity> CreatePickerEntityForSpecificClaimTypes(string input, List<ClaimTypeConfig> claimTypesToResolve, bool inputHasKeyword)
        {
            List<PickerEntity> entities = new List<PickerEntity>();
            foreach (var claimTypeToResolve in claimTypesToResolve)
            {
                PickerEntity pe = CreatePickerEntity();
                SPClaim claim = CreateClaim(claimTypeToResolve.ClaimType, input, claimTypeToResolve.ClaimValueType);

                if (String.Equals(claim.ClaimType, IdentityClaimTypeConfig.ClaimType, StringComparison.InvariantCultureIgnoreCase))
                {
                    pe.DisplayText = input;
                }
                else
                {
                    pe.DisplayText = String.Format(
                        PickerEntityDisplayText,
                        claimTypeToResolve.ClaimTypeDisplayName,
                        input);
                }

                pe.EntityType = claimTypeToResolve.DirectoryObjectType == AzureADObjectType.User ? SPClaimEntityTypes.User : ClaimsProviderConstants.GroupClaimEntityType;
                pe.Description = String.Format(
                    PickerEntityOnMouseOver,
                    claimTypeToResolve.DirectoryObjectProperty.ToString(),
                    input);

                pe.Claim = claim;
                pe.IsResolved = true;
                //pe.EntityGroupName = "";

                if (!String.IsNullOrEmpty(claimTypeToResolve.EntityDataKey))
                {
                    pe.EntityData[claimTypeToResolve.EntityDataKey] = pe.Claim.Value;
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Added metadata '{claimTypeToResolve.EntityDataKey}' with value '{pe.EntityData[claimTypeToResolve.EntityDataKey]}' to new entity", TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
                }
                entities.Add(pe);
                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Created entity: display text: '{pe.DisplayText}', value: '{pe.Claim.Value}', claim type: '{pe.Claim.ClaimType}'.", TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
            }
            return entities.Count > 0 ? entities : null;
        }

        /// <summary>
        /// Called when claims provider is added to the farm. At this point the persisted object is not created yet so we can't pass actual claim type list
        /// If assemblyBinding for Newtonsoft.Json was not correctly added on the server, this method will generate an assembly load exception during feature activation
        /// Also called every 1st query in people picker
        /// </summary>
        /// <param name="claimTypes"></param>
        protected override void FillClaimTypes(List<string> claimTypes)
        {
            if (claimTypes == null) return;
            try
            {
                this.Lock_Config.EnterReadLock();
                if (ProcessedClaimTypesList == null) return;
                foreach (var claimTypeSettings in ProcessedClaimTypesList)
                {
                    claimTypes.Add(claimTypeSettings.ClaimType);
                }
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in FillClaimTypes", TraceCategory.Core, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
        }

        protected override void FillClaimValueTypes(List<string> claimValueTypes)
        {
            claimValueTypes.Add(WIF4_5.ClaimValueTypes.String);
        }

        protected override void FillClaimsForEntity(Uri context, SPClaim entity, SPClaimProviderContext claimProviderContext, List<SPClaim> claims)
        {
            AugmentEntity(context, entity, claimProviderContext, claims);
        }

        protected override void FillClaimsForEntity(Uri context, SPClaim entity, List<SPClaim> claims)
        {
            AugmentEntity(context, entity, null, claims);
        }

        /// <summary>
        /// Perform augmentation of entity supplied
        /// </summary>
        /// <param name="context"></param>
        /// <param name="entity">entity to augment</param>
        /// <param name="claimProviderContext">Can be null</param>
        /// <param name="claims"></param>
        protected virtual void AugmentEntity(Uri context, SPClaim entity, SPClaimProviderContext claimProviderContext, List<SPClaim> claims)
        {
            Stopwatch timer = new Stopwatch();
            timer.Start();
            SPClaim decodedEntity;
            if (SPClaimProviderManager.IsUserIdentifierClaim(entity))
                decodedEntity = SPClaimProviderManager.DecodeUserIdentifierClaim(entity);
            else
            {
                if (SPClaimProviderManager.IsEncodedClaim(entity.Value))
                    decodedEntity = SPClaimProviderManager.Local.DecodeClaim(entity.Value);
                else
                    decodedEntity = entity;
            }

            SPOriginalIssuerType loginType = SPOriginalIssuers.GetIssuerType(decodedEntity.OriginalIssuer);
            if (loginType != SPOriginalIssuerType.TrustedProvider && loginType != SPOriginalIssuerType.ClaimProvider)
            {
                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Not trying to augment '{decodedEntity.Value}' because OriginalIssuer is '{decodedEntity.OriginalIssuer}'.",
                    TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Augmentation);
                return;
            }

            if (!Initialize(context, null))
                return;

            this.Lock_Config.EnterReadLock();
            try
            {
                if (!this.CurrentConfiguration.EnableAugmentation)
                    return;

                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Starting augmentation for user '{decodedEntity.Value}'.", TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Augmentation);
                ClaimTypeConfig groupClaimTypeSettings = this.ProcessedClaimTypesList.FirstOrDefault(x => x.DirectoryObjectType == AzureADObjectType.Group);
                if (groupClaimTypeSettings == null)
                {
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] No role claim type with SPClaimEntityTypes set to 'FormsRole' was found, please check claims mapping table.",
                        TraceSeverity.High, EventSeverity.Error, TraceCategory.Augmentation);
                    return;
                }

                OperationContext infos = new OperationContext(CurrentConfiguration, OperationType.Augmentation, ProcessedClaimTypesList, null, decodedEntity, context, null, null, Int32.MaxValue);
                Task<List<SPClaim>> resultsTask = GetGroupMembershipAsync(infos, groupClaimTypeSettings);
                resultsTask.Wait();
                List<SPClaim> groups = resultsTask.Result;
                timer.Stop();
                if (groups?.Count > 0)
                {
                    foreach (SPClaim group in groups)
                    {
                        claims.Add(group);
                        ClaimsProviderLogging.Log($"[{ProviderInternalName}] Added group '{group.Value}' to user '{infos.IncomingEntity.Value}'",
                            TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Augmentation);
                    }
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] User '{infos.IncomingEntity.Value}' was augmented with {groups.Count.ToString()} groups in {timer.ElapsedMilliseconds.ToString()} ms",
                        TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Augmentation);
                }
                else
                {
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] No group found for user '{infos.IncomingEntity.Value}', search took {timer.ElapsedMilliseconds.ToString()} ms",
                        TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Augmentation);
                }
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in AugmentEntity", TraceCategory.Augmentation, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
        }

        protected async virtual Task<List<SPClaim>> GetGroupMembershipAsync(OperationContext requestInfo, ClaimTypeConfig groupClaimTypeSettings)
        {
            List<SPClaim> claims = new List<SPClaim>();
            foreach (var tenant in this.CurrentConfiguration.AzureTenants)
            {
                // The logic is that there will always be only 1 tenant returning groups, so as soon as 1 returned groups, foreach can stop
                claims = await GetGroupMembershipFromAzureADAsync(requestInfo, groupClaimTypeSettings, tenant).ConfigureAwait(false);
                if (claims?.Count > 0) break;
            }
            return claims;
        }

        protected async virtual Task<List<SPClaim>> GetGroupMembershipFromAzureADAsync(OperationContext requestInfo, ClaimTypeConfig groupClaimTypeSettings, AzureTenant tenant)
        {
            List<SPClaim> claims = new List<SPClaim>();
            var userResult = await tenant.GraphService.Users.Request().Filter($"{requestInfo.CurrentClaimTypeConfig.DirectoryObjectProperty} eq '{requestInfo.IncomingEntity.Value}'").GetAsync().ConfigureAwait(false);
            User user = userResult.FirstOrDefault();
            if (user == null) return claims;
            // This only returns a collection of strings, set with group ID:
            //IDirectoryObjectGetMemberGroupsCollectionPage groups = await tenant.GraphService.Users[requestInfo.IncomingEntity.Value].GetMemberGroups(true).Request().PostAsync().ConfigureAwait(false);
            IUserMemberOfCollectionWithReferencesPage groups = await tenant.GraphService.Users[requestInfo.IncomingEntity.Value].MemberOf.Request().GetAsync().ConfigureAwait(false);
            bool continueProcess = groups?.Count > 0;
            while (continueProcess)
            {
                foreach (Group group in groups.OfType<Group>())
                {
                    string groupClaimValue = GetPropertyValue(group, groupClaimTypeSettings.DirectoryObjectProperty.ToString());
                    claims.Add(CreateClaim(groupClaimTypeSettings.ClaimType, groupClaimValue, groupClaimTypeSettings.ClaimValueType));
                }
                if (groups.NextPageRequest != null) groups = await groups.NextPageRequest.GetAsync().ConfigureAwait(false);
                else continueProcess = false;
            }
            return claims;
        }

        protected override void FillEntityTypes(List<string> entityTypes)
        {
            entityTypes.Add(SPClaimEntityTypes.User);
            entityTypes.Add(ClaimsProviderConstants.GroupClaimEntityType);
        }

        protected override void FillHierarchy(Uri context, string[] entityTypes, string hierarchyNodeID, int numberOfLevels, Microsoft.SharePoint.WebControls.SPProviderHierarchyTree hierarchy)
        {
            List<AzureADObjectType> aadEntityTypes = new List<AzureADObjectType>();
            if (entityTypes.Contains(SPClaimEntityTypes.User))
                aadEntityTypes.Add(AzureADObjectType.User);
            if (entityTypes.Contains(ClaimsProviderConstants.GroupClaimEntityType))
                aadEntityTypes.Add(AzureADObjectType.Group);

            if (!Initialize(context, entityTypes))
                return;

            this.Lock_Config.EnterReadLock();
            try
            {
                if (hierarchyNodeID == null)
                {
                    // Root level
                    foreach (var azureObject in this.ProcessedClaimTypesList.FindAll(x => !x.UseMainClaimTypeOfDirectoryObject && aadEntityTypes.Contains(x.DirectoryObjectType)))
                    {
                        hierarchy.AddChild(
                            new Microsoft.SharePoint.WebControls.SPProviderHierarchyNode(
                                _ProviderInternalName,
                                azureObject.ClaimTypeDisplayName,
                                azureObject.ClaimType,
                                true));
                    }
                }
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in FillHierarchy", TraceCategory.Claims_Picking, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
        }

        /// <summary>
        /// Override this method to change / remove permissions created by AzureCP, or add new ones
        /// </summary>
        /// <param name="currentContext"></param>
        /// <param name="entityTypes"></param>
        /// <param name="input"></param>
        /// <param name="resolved">List of permissions created by LDAPCP</param>
        protected virtual void FillPermissions(OperationContext currentContext, ref List<PickerEntity> resolved)
        {
        }

        protected override void FillResolve(Uri context, string[] entityTypes, SPClaim resolveInput, List<Microsoft.SharePoint.WebControls.PickerEntity> resolved)
        {
            ClaimsProviderLogging.LogDebug($"context passed to FillResolve (SPClaim): {context.ToString()}");
            if (!Initialize(context, entityTypes))
                return;

            // Ensure incoming claim should be validated by AzureCP
            // Must be made after call to Initialize because SPTrustedLoginProvider name must be known
            if (!String.Equals(resolveInput.OriginalIssuer, IssuerName, StringComparison.InvariantCultureIgnoreCase))
                return;

            this.Lock_Config.EnterReadLock();
            try
            {
                OperationContext infos = new OperationContext(CurrentConfiguration, OperationType.Validation, ProcessedClaimTypesList, resolveInput.Value, resolveInput, context, entityTypes, null, Int32.MaxValue);
                List<PickerEntity> permissions = SearchOrValidate(infos);
                if (permissions.Count == 1)
                {
                    resolved.Add(permissions[0]);
                    ClaimsProviderLogging.Log(String.Format("[{0}] Validated entity: claim value: '{1}', claim type: '{2}'", ProviderInternalName, permissions[0].Claim.Value, permissions[0].Claim.ClaimType),
                        TraceSeverity.High, EventSeverity.Information, TraceCategory.Claims_Picking);
                }
                else
                {
                    ClaimsProviderLogging.Log(String.Format("[{0}] Validation of incoming claim returned {1} entities instead of 1 expected. Aborting operation", ProviderInternalName, permissions.Count.ToString()), TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Claims_Picking);
                }
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in FillResolve(SPClaim)", TraceCategory.Claims_Picking, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
        }

        protected override void FillResolve(Uri context, string[] entityTypes, string resolveInput, List<Microsoft.SharePoint.WebControls.PickerEntity> resolved)
        {
            if (!Initialize(context, entityTypes))
                return;

            this.Lock_Config.EnterReadLock();
            try
            {
                OperationContext currentContext = new OperationContext(CurrentConfiguration, OperationType.Search, ProcessedClaimTypesList, resolveInput, null, context, entityTypes, null, Int32.MaxValue);
                List<PickerEntity> permissions = SearchOrValidate(currentContext);
                FillPermissions(currentContext, ref permissions);
                foreach (PickerEntity entity in permissions)
                {
                    resolved.Add(entity);
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Added entity: claim value: '{entity.Claim.Value}', claim type: '{entity.Claim.ClaimType}'",
                        TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Claims_Picking);
                }

                if (permissions?.Count > 0)
                {
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Returned {permissions.Count} entities with input '{currentContext.Input}'",
                        TraceSeverity.High, EventSeverity.Information, TraceCategory.Claims_Picking);
                }
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in FillResolve(string)", TraceCategory.Claims_Picking, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
        }

        protected override void FillSchema(Microsoft.SharePoint.WebControls.SPProviderSchema schema)
        {
            schema.AddSchemaElement(new SPSchemaElement(PeopleEditorEntityDataKeys.DisplayName, "Display Name", SPSchemaElementType.Both));
        }

        protected override void FillSearch(Uri context, string[] entityTypes, string searchPattern, string hierarchyNodeID, int maxCount, Microsoft.SharePoint.WebControls.SPProviderHierarchyTree searchTree)
        {
            if (!Initialize(context, entityTypes))
                return;

            this.Lock_Config.EnterReadLock();
            try
            {
                OperationContext currentContext = new OperationContext(CurrentConfiguration, OperationType.Search, ProcessedClaimTypesList, searchPattern, null, context, entityTypes, hierarchyNodeID, maxCount);
                List<PickerEntity> permissions = SearchOrValidate(currentContext);
                FillPermissions(currentContext, ref permissions);
                SPProviderHierarchyNode matchNode = null;
                foreach (PickerEntity entity in permissions)
                {
                    // Add current PickerEntity to the corresponding ClaimType in the hierarchy
                    if (searchTree.HasChild(entity.Claim.ClaimType))
                    {
                        matchNode = searchTree.Children.First(x => x.HierarchyNodeID == entity.Claim.ClaimType);
                    }
                    else
                    {
                        ClaimTypeConfig ctConfig = ProcessedClaimTypesList.FirstOrDefault(x =>
                            !x.UseMainClaimTypeOfDirectoryObject &&
                            String.Equals(x.ClaimType, entity.Claim.ClaimType, StringComparison.InvariantCultureIgnoreCase));

                        string nodeName = ctConfig != null ? ctConfig.ClaimTypeDisplayName : entity.Claim.ClaimType;
                        matchNode = new SPProviderHierarchyNode(_ProviderInternalName, nodeName, entity.Claim.ClaimType, true);
                        searchTree.AddChild(matchNode);
                    }
                    matchNode.AddEntity(entity);
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Added entity: claim value: '{entity.Claim.Value}', claim type: '{entity.Claim.ClaimType}'",
                        TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Claims_Picking);
                }

                if (permissions?.Count > 0)
                {
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Returned {permissions.Count} entities with input '{currentContext.Input}'", 
                        TraceSeverity.High, EventSeverity.Information, TraceCategory.Claims_Picking);
                }
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in FillSearch", TraceCategory.Claims_Picking, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
        }

        /// <summary>
        /// Search or validate incoming input or entity
        /// </summary>
        /// <param name="currentContext">Information about current context and operation</param>
        /// <returns></returns>
        protected virtual List<PickerEntity> SearchOrValidate(OperationContext currentContext)
        {
            List<PickerEntity> permissions = new List<PickerEntity>();
            try
            {
                if (this.CurrentConfiguration.AlwaysResolveUserInput)
                {
                    // Completely bypass query to Azure AD
                    List<PickerEntity> entities = CreatePickerEntityForSpecificClaimTypes(
                        currentContext.Input,
                        currentContext.CurrentClaimTypeConfigList.FindAll(x => !x.UseMainClaimTypeOfDirectoryObject),
                        false);
                    if (entities != null)
                    {
                        foreach (var entity in entities)
                        {
                            permissions.Add(entity);
                            ClaimsProviderLogging.Log($"[{ProviderInternalName}] Added entity created with no query sent to Azure AD because AzureCP is configured to bypass Azure AD and always validate input: claim value: '{entity.Claim.Value}', claim type: '{entity.Claim.ClaimType}'",
                                TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
                        }
                    }
                    return permissions;
                }

                if (currentContext.OperationType == OperationType.Search)
                {
                    // Check if input starts with a prefix configured on a ClaimTypeConfig. If so an entity should be returned using ClaimTypeConfig found
                    // ClaimTypeConfigEnsureUniquePrefixToBypassLookup ensures that collection cannot contain duplicates
                    ClaimTypeConfig ctConfigWithInputPrefixMatch = currentContext.CurrentClaimTypeConfigList.FirstOrDefault(x =>
                        !String.IsNullOrEmpty(x.PrefixToBypassLookup) &&
                        currentContext.Input.StartsWith(x.PrefixToBypassLookup, StringComparison.InvariantCultureIgnoreCase));
                    if (ctConfigWithInputPrefixMatch != null)
                    {
                        currentContext.Input = currentContext.Input.Substring(ctConfigWithInputPrefixMatch.PrefixToBypassLookup.Length);
                        if (String.IsNullOrEmpty(currentContext.Input))
                        {
                            // No value in the input after the prefix, return
                            return permissions;
                        }
                        PickerEntity entity = CreatePickerEntityForSpecificClaimType(
                            currentContext.Input,
                            ctConfigWithInputPrefixMatch,
                            true);
                        if (entity != null)
                        {
                            permissions.Add(entity);
                            ClaimsProviderLogging.Log($"[{ProviderInternalName}] Created entity with no query sent to Azure AD because input started with prefix '{ctConfigWithInputPrefixMatch.PrefixToBypassLookup}', which is configured for claim type '{ctConfigWithInputPrefixMatch.ClaimType}'. Claim value: '{entity.Claim.Value}', claim type: '{entity.Claim.ClaimType}'",
                                TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
                            return permissions;
                        }
                    }
                    SearchOrValidateInAzureAD(currentContext, ref permissions);
                }
                else if (currentContext.OperationType == OperationType.Validation)
                {
                    SearchOrValidateInAzureAD(currentContext, ref permissions);
                    if (!String.IsNullOrEmpty(currentContext.CurrentClaimTypeConfig.PrefixToBypassLookup))
                    {
                        // At this stage, it is impossible to know if entity was originally created with the keyword that query to Azure AD
                        // But it should be always validated since property PrefixToBypassLookup is set for this ClaimTypeConfig
                        if (permissions.Count == 1) return permissions;

                        // If Azure AD didn't find a result, create entity manually
                        PickerEntity entity = CreatePickerEntityForSpecificClaimType(
                            currentContext.Input,
                            currentContext.CurrentClaimTypeConfig,
                            currentContext.InputHasKeyword);
                        if (entity != null)
                        {
                            permissions.Add(entity);
                            ClaimsProviderLogging.Log($"[{ProviderInternalName}] Validated entity with no query sent to Azure AD because its claim type ('{currentContext.CurrentClaimTypeConfig.ClaimType}') has property 'PrefixToBypassLookup' set in AzureCPConfig.ClaimTypes: Claim value: '{entity.Claim.Value}', claim type: '{entity.Claim.ClaimType}'",
                                TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
                        }
                        return permissions;
                    }
                }
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in SearchOrValidate", TraceCategory.Claims_Picking, ex);
            }
            return permissions;
        }

        protected virtual void SearchOrValidateInAzureAD(OperationContext currentContext, ref List<PickerEntity> permissions)
        {
            string userFilter = String.Empty;
            string groupFilter = String.Empty;
            string userSelect = String.Empty;
            string groupSelect = String.Empty;
            BuildFilter(currentContext, out userFilter, out groupFilter, out userSelect, out groupSelect);

            List<AzureADResult> aadResults = null;
            using (new SPMonitoredScope($"[{ProviderInternalName}] Total time spent to query Azure AD tenant(s)", 1000))
            {
                // Call async method in a task to avoid error "Asynchronous operations are not allowed in this context" error when permission is validated (POST from people picker)
                // More info on the error: https://stackoverflow.com/questions/672237/running-an-asynchronous-operation-triggered-by-an-asp-net-web-page-request
                Task azureADQueryTask = Task.Run(async () =>
                {
                    aadResults = await QueryAzureADTenantsAsync(currentContext, userFilter, groupFilter, userSelect, groupSelect).ConfigureAwait(false);
                });
                azureADQueryTask.Wait();
            }

            if (aadResults?.Count > 0)
            {
                List<AzureCPResult> results = ProcessAzureADResults(currentContext, aadResults);
                if (results?.Count > 0)
                {
                    foreach (var result in results)
                    {
                        permissions.Add(result.PickerEntity);
                        ClaimsProviderLogging.Log($"[{ProviderInternalName}] Added entity returned by Azure AD: claim value: '{result.PickerEntity.Claim.Value}', claim type: '{result.PickerEntity.Claim.ClaimType}'",
                            TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
                    }
                }
            }
        }

        /// <summary>
        /// Build filter and select statements sent to Azure AD
        /// $filter and $select must be URL encoded as documented in https://developer.microsoft.com/en-us/graph/docs/concepts/query_parameters#encoding-query-parameters
        /// </summary>
        /// <param name="currentContext"></param>
        /// <param name="userFilter">User filter</param>
        /// <param name="groupFilter">Group filter</param>
        /// <param name="userSelect">User properties to get from AAD</param>
        /// <param name="groupSelect">Group properties to get from AAD</param>
        protected virtual void BuildFilter(OperationContext currentContext, out string userFilter, out string groupFilter, out string userSelect, out string groupSelect)
        {
            StringBuilder userFilterBuilder = new StringBuilder("accountEnabled eq true and (");
            StringBuilder groupFilterBuilder = new StringBuilder();
            StringBuilder userSelectBuilder = new StringBuilder("UserType, Mail, ");    // UserType and Mail are always needed to deal with Guest users
            StringBuilder groupSelectBuilder = new StringBuilder("Id, ");               // Id is always required for groups

            string preferredFilterPattern;
            string input = currentContext.Input;
            if (currentContext.ExactSearch) preferredFilterPattern = String.Format(ClaimsProviderConstants.SearchPatternEquals, "{0}", input);
            else preferredFilterPattern = String.Format(ClaimsProviderConstants.SearchPatternStartsWith, "{0}", input);

            bool firstUserObjectProcessed = false;
            bool firstGroupObjectProcessed = false;
            foreach (ClaimTypeConfig ctConfig in currentContext.CurrentClaimTypeConfigList)
            {
                string currentPropertyString = ctConfig.DirectoryObjectProperty.ToString();
                string currentFilter;
                if (!ctConfig.SupportsWildcard)
                    currentFilter = String.Format(ClaimsProviderConstants.SearchPatternEquals, currentPropertyString, input);
                else
                    currentFilter = String.Format(preferredFilterPattern, currentPropertyString);

                // Id needs a specific check: input must be a valid GUID AND equals filter must be used, otherwise Azure AD will throw an error
                if (ctConfig.DirectoryObjectProperty == AzureADObjectProperty.Id)
                {
                    Guid idGuid = new Guid();
                    if (!Guid.TryParse(input, out idGuid)) continue;
                    else currentFilter = String.Format(ClaimsProviderConstants.SearchPatternEquals, currentPropertyString, idGuid.ToString());
                }

                if (ctConfig.DirectoryObjectType == AzureADObjectType.User)
                {
                    if (!firstUserObjectProcessed) firstUserObjectProcessed = true;
                    else
                    {
                        currentFilter = " or " + currentFilter;
                        currentPropertyString = ", " + currentPropertyString;
                    }
                    userFilterBuilder.Append(currentFilter);
                    userSelectBuilder.Append(currentPropertyString);
                }
                else
                {
                    // else with no further test assumes everything that is not a User is a Group
                    if (!firstGroupObjectProcessed) firstGroupObjectProcessed = true;
                    else
                    {
                        currentFilter = currentFilter + " or ";
                        currentPropertyString = ", " + currentPropertyString;
                    }
                    groupFilterBuilder.Append(currentFilter);
                    groupSelectBuilder.Append(currentPropertyString);
                }
            }

            // Also add metadata properties to $select of corresponding object type
            if (firstUserObjectProcessed)
            {
                foreach (ClaimTypeConfig ctConfig in MetadataConfig.Where( x => x.DirectoryObjectType == AzureADObjectType.User))
                {
                    userSelectBuilder.Append($", {ctConfig.DirectoryObjectProperty.ToString()}");
                }
            }
            if (firstGroupObjectProcessed)
            {
                foreach (ClaimTypeConfig ctConfig in MetadataConfig.Where(x => x.DirectoryObjectType == AzureADObjectType.Group))
                {
                    groupSelectBuilder.Append($", {ctConfig.DirectoryObjectProperty.ToString()}");
                }
            }

            userFilterBuilder.Append(")");  // Closing of accountEnabled

            // Clear user/group filters if no corresponding object was found in requestInfo.ClaimTypeConfigList
            if (!firstUserObjectProcessed) userFilterBuilder.Clear();
            if (!firstGroupObjectProcessed) groupFilterBuilder.Clear();

            userFilter = HttpUtility.UrlEncode(userFilterBuilder.ToString());
            groupFilter = HttpUtility.UrlEncode(groupFilterBuilder.ToString());
            userSelect = HttpUtility.UrlEncode(userSelectBuilder.ToString());
            groupSelect = HttpUtility.UrlEncode(groupSelectBuilder.ToString());
        }

        protected virtual async Task<List<AzureADResult>> QueryAzureADTenantsAsync(OperationContext currentContext, string userFilter, string groupFilter, string userSelect, string groupSelect)
        {
            if (userFilter == null && groupFilter == null) return null;
            List<AzureADResult> allSearchResults = new List<AzureADResult>();
            var lockResults = new object();

            //foreach (AzureTenant coco in this.CurrentConfiguration.AzureTenants)
            Parallel.ForEach(this.CurrentConfiguration.AzureTenants, async coco =>
            //var queryTenantTasks = this.CurrentConfiguration.AzureTenants.Select (async coco =>
            {
                Stopwatch timer = new Stopwatch();
                AzureADResult searchResult = null;
                try
                {
                    timer.Start();
                    searchResult = await QueryAzureADTenantAsync(currentContext, coco, userFilter, groupFilter, userSelect, groupSelect, true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ClaimsProviderLogging.LogException(ProviderInternalName, String.Format("in QueryAzureADTenantsAsync while querying tenant {0}", coco.TenantName), TraceCategory.Lookup, ex);
                }
                finally
                {
                    timer.Stop();
                }

                if (searchResult != null)
                {
                    lock (lockResults)
                    {
                        allSearchResults.Add(searchResult);
                    }
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Got {searchResult.UserOrGroupResultList.Count().ToString()} users/groups and {searchResult.DomainsRegisteredInAzureADTenant.Count().ToString()} registered domains in {timer.ElapsedMilliseconds.ToString()} ms from '{coco.TenantName}' with input '{currentContext.Input}'",
                        TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Lookup);
                }
                else ClaimsProviderLogging.Log($"[{ProviderInternalName}] Got no result from '{coco.TenantName}' with input '{currentContext.Input}', search took {timer.ElapsedMilliseconds.ToString()} ms", TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Lookup);
            });
            //}
            return allSearchResults;
        }

        protected virtual async Task<AzureADResult> QueryAzureADTenantAsync(OperationContext currentContext, AzureTenant coco, string userFilter, string groupFilter, string userSelect, string groupSelect, bool firstAttempt)
        {
            ClaimsProviderLogging.Log($"[{ProviderInternalName}] Querying Azure AD tenant '{coco.TenantName}' for users/groups/domains, with input '{currentContext.Input}'", TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Lookup);
            AzureADResult tenantResults = new AzureADResult();
            bool tryAgain = false;
            object lockAddResultToCollection = new object();
            CancellationTokenSource cts = new CancellationTokenSource(ClaimsProviderConstants.timeout);
            try
            {
                using (new SPMonitoredScope($"[{ProviderInternalName}] Querying Azure AD tenant '{coco.TenantName}' for users/groups/domains, with input '{currentContext.Input}'", 1000))
                {
                    // No need to lock here: as per https://stackoverflow.com/questions/49108179/need-advice-on-getting-access-token-with-multiple-task-in-microsoft-graph:
                    // The Graph client object is thread-safe and re-entrant
                    Task userQueryTask = Task.Run(async () =>
                    {
                        ClaimsProviderLogging.LogDebug($"[{ProviderInternalName}] UserQueryTask starting for tenant '{coco.TenantName}'");
                        if (String.IsNullOrEmpty(userFilter)) return;
                        IGraphServiceUsersCollectionPage users = await coco.GraphService.Users.Request().Select(userSelect).Filter(userFilter).GetAsync();
                        if (users?.Count > 0)
                        {
                            do
                            {
                                lock (lockAddResultToCollection)
                                {
                                    tenantResults.UserOrGroupResultList.AddRange(users.CurrentPage);
                                }
                                if (users.NextPageRequest != null) users = await users.NextPageRequest.GetAsync().ConfigureAwait(false);
                            }
                            while (users?.Count > 0 && users.NextPageRequest != null);
                        }
                        ClaimsProviderLogging.LogDebug($"[{ProviderInternalName}] UserQueryTask ended for tenant '{coco.TenantName}'");
                    }, cts.Token);
                    Task groupQueryTask = Task.Run(async () =>
                    {
                        ClaimsProviderLogging.LogDebug($"[{ProviderInternalName}] GroupQueryTask starting for tenant '{coco.TenantName}'");
                        if (String.IsNullOrEmpty(groupFilter)) return;
                        IGraphServiceGroupsCollectionPage groups = await coco.GraphService.Groups.Request().Select(groupSelect).Filter(groupFilter).GetAsync();
                        if (groups?.Count > 0)
                        {
                            do
                            {
                                lock (lockAddResultToCollection)
                                {
                                    tenantResults.UserOrGroupResultList.AddRange(groups.CurrentPage);
                                }
                                if (groups.NextPageRequest != null) groups = await groups.NextPageRequest.GetAsync().ConfigureAwait(false);
                            }
                            while (groups?.Count > 0 && groups.NextPageRequest != null);
                        }
                        ClaimsProviderLogging.LogDebug($"[{ProviderInternalName}] GroupQueryTask ended for tenant '{coco.TenantName}'");
                    }, cts.Token);
                    Task domainQueryTask = Task.Run(async () =>
                    {
                        ClaimsProviderLogging.LogDebug($"[{ProviderInternalName}] DomainQueryTask starting for tenant '{coco.TenantName}'");
                        IGraphServiceDomainsCollectionPage domains = await coco.GraphService.Domains.Request().GetAsync();
                        lock (lockAddResultToCollection)
                        {
                            tenantResults.DomainsRegisteredInAzureADTenant.AddRange(domains.Where(x => x.IsVerified == true).Select(x => x.Id));
                        }
                        ClaimsProviderLogging.LogDebug($"[{ProviderInternalName}] DomainQueryTask ended for tenant '{coco.TenantName}'");
                    }, cts.Token);

                    Task.WaitAll(new Task[3] { userQueryTask, groupQueryTask, domainQueryTask }, ClaimsProviderConstants.timeout, cts.Token);
                    //await Task.WhenAll(userQueryTask, groupQueryTask).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Query on Azure AD tenant '{coco.TenantName}' exceeded timeout of {ClaimsProviderConstants.timeout} ms and was cancelled.", TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Lookup);
                tryAgain = true;
            }
            catch (AggregateException ex)
            {
                // Task.WaitAll throws an AggregateException, which contains all exceptions thrown by tasks it waited on
                ClaimsProviderLogging.LogException(ProviderInternalName, $"while querying tenant '{coco.TenantName}'", TraceCategory.Lookup, ex);
                tryAgain = true;
            }
            finally
            {
                ClaimsProviderLogging.LogDebug($"[{ProviderInternalName}] End of query for tenant '{coco.TenantName}'");
                cts.Dispose();
            }

            if (firstAttempt && tryAgain)
            {
                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Doing new attempt to query tenant '{coco.TenantName}'...",
                    TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Lookup);
                tenantResults = await QueryAzureADTenantAsync(currentContext, coco, userFilter, groupFilter, userSelect, groupSelect, false).ConfigureAwait(false);
            }
            return tenantResults;
        }

        protected virtual List<AzureCPResult> ProcessAzureADResults(OperationContext currentContext, List<AzureADResult> azureADResults)
        {
            // Split results between users/groups and list of registered domains in the tenant
            List<DirectoryObject> usersAndGroupsResults = new List<DirectoryObject>();
            List<string> domains = new List<string>();
            // For each Azure AD tenant
            foreach (AzureADResult tenantResults in azureADResults)
            {
                usersAndGroupsResults.AddRange(tenantResults.UserOrGroupResultList);
                domains.AddRange(tenantResults.DomainsRegisteredInAzureADTenant);
            }

            // Return if no user / groups is found, or if no registered domain is found
            if (usersAndGroupsResults == null || !usersAndGroupsResults.Any() || domains == null || !domains.Any())
            {
                return null;
            };

            // If exactSearch is true, we don't care about attributes with UseMainClaimTypeOfDirectoryObject = true
            List<ClaimTypeConfig> claimTypeConfigList;
            if (currentContext.ExactSearch) claimTypeConfigList = currentContext.CurrentClaimTypeConfigList.FindAll(x => !x.UseMainClaimTypeOfDirectoryObject);
            else claimTypeConfigList = currentContext.CurrentClaimTypeConfigList;

            List<AzureCPResult> processedResults = new List<AzureCPResult>();
            foreach (DirectoryObject userOrGroup in usersAndGroupsResults)
            {
                DirectoryObject currentObject = null;
                AzureADObjectType objectType;
                if (userOrGroup is User)
                {
                    // Always skip shadow users: UserType is Guest and his mail matches a verified domain in AAD tenant
                    string userType = GetPropertyValue(userOrGroup, AzureADUserTypeHelper.PropertyNameContainingUserType);
                    if (String.IsNullOrEmpty(userType))
                    {
                        ClaimsProviderLogging.Log(
                            String.Format("[{0}] User {1} filtered out because his property UserType is empty.", ProviderInternalName, ((User)userOrGroup).UserPrincipalName),
                            TraceSeverity.Unexpected, EventSeverity.Warning, TraceCategory.Lookup);
                        continue;
                    }
                    if (String.Equals(userType, AzureADUserTypeHelper.GuestUserType, StringComparison.InvariantCultureIgnoreCase))
                    {
                        string mail = GetPropertyValue(userOrGroup, "Mail");
                        if (String.IsNullOrEmpty(mail))
                        {
                            ClaimsProviderLogging.Log(
                                String.Format("[{0}] Guest user {1} filtered out because his mail is empty.", ProviderInternalName, ((User)userOrGroup).UserPrincipalName),
                                TraceSeverity.Unexpected, EventSeverity.Warning, TraceCategory.Lookup);
                            continue;
                        }
                        if (!mail.Contains('@')) continue;
                        string maildomain = mail.Split('@')[1];
                        if (domains.Any(x => String.Equals(x, maildomain, StringComparison.InvariantCultureIgnoreCase)))
                        {
                            ClaimsProviderLogging.Log(
                                String.Format("[{0}] Guest user {1} filtered out because he is in a domain registered in AAD tenant.", ProviderInternalName, mail),
                                TraceSeverity.Verbose, EventSeverity.Verbose, TraceCategory.Lookup);
                            continue;
                        }
                    }
                    currentObject = userOrGroup;
                    objectType = AzureADObjectType.User;
                }
                else
                {
                    currentObject = userOrGroup;
                    objectType = AzureADObjectType.Group;
                }

                foreach (ClaimTypeConfig currentClaimTypeConfig in claimTypeConfigList.Where(x => x.DirectoryObjectType == objectType))
                {
                    // Get value with of current GraphProperty
                    string directoryObjectPropertyValue = GetPropertyValue(currentObject, currentClaimTypeConfig.DirectoryObjectProperty.ToString());

                    // Check if property exists (no null) and has a value (not String.Empty)
                    if (String.IsNullOrEmpty(directoryObjectPropertyValue)) continue;

                    // Check if current value mathes input, otherwise go to next GraphProperty to check
                    if (currentContext.ExactSearch)
                    {
                        if (!String.Equals(directoryObjectPropertyValue, currentContext.Input, StringComparison.InvariantCultureIgnoreCase)) continue;
                    }
                    else
                    {
                        if (!directoryObjectPropertyValue.StartsWith(currentContext.Input, StringComparison.InvariantCultureIgnoreCase)) continue;
                    }

                    // Current DirectoryObjectProperty value matches user input. Add current result to search results if it is not already present
                    string queryMatchValue = directoryObjectPropertyValue;
                    string valueToUseInClaimValue = directoryObjectPropertyValue;
                    ClaimTypeConfig claimTypeConfigToCompare;
                    if (currentClaimTypeConfig.UseMainClaimTypeOfDirectoryObject)
                    {
                        if (objectType == AzureADObjectType.User)
                        {
                            claimTypeConfigToCompare = IdentityClaimTypeConfig;
                        }
                        else
                        {
                            claimTypeConfigToCompare = MainGroupClaimTypeConfig;                            
                        }
                        // Get the value of the DirectoryObjectProperty linked to current directory object
                        valueToUseInClaimValue = GetPropertyValue(currentObject, claimTypeConfigToCompare.DirectoryObjectProperty.ToString());
                        if (String.IsNullOrEmpty(valueToUseInClaimValue)) continue;
                    }
                    else
                    {
                        claimTypeConfigToCompare = currentClaimTypeConfig;
                    }

                    // if claim type and claim value already exists, skip
                    bool resultAlreadyExists = processedResults.Exists(x =>
                        String.Equals(x.ClaimTypeConfig.ClaimType, claimTypeConfigToCompare.ClaimType, StringComparison.InvariantCultureIgnoreCase) &&
                        String.Equals(x.PermissionValue, valueToUseInClaimValue, StringComparison.InvariantCultureIgnoreCase));
                    if (resultAlreadyExists) continue;

                    // Passed the checks, add it to the processedResults list
                    processedResults.Add(
                        new AzureCPResult(currentObject)
                        {
                            ClaimTypeConfig = currentClaimTypeConfig,
                            PermissionValue = valueToUseInClaimValue,
                            QueryMatchValue = queryMatchValue,
                        });
                }
            }

            ClaimsProviderLogging.Log($"[{ProviderInternalName}] {processedResults.Count} permission(s) to create after filtering", TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Lookup);
            foreach (AzureCPResult result in processedResults)
            {
                PickerEntity pe = CreatePickerEntityHelper(result);
                result.PickerEntity = pe;
            }
            return processedResults;
        }

        public override string Name { get { return ProviderInternalName; } }
        public override bool SupportsEntityInformation { get { return true; } }
        public override bool SupportsHierarchy { get { return true; } }
        public override bool SupportsResolve { get { return true; } }
        public override bool SupportsSearch { get { return true; } }
        public override bool SupportsUserKey { get { return true; } }

        /// <summary>
        /// Return the identity claim type
        /// </summary>
        /// <returns></returns>
        public override string GetClaimTypeForUserKey()
        {
            if (!Initialize(null, null))
                return null;

            this.Lock_Config.EnterReadLock();
            try
            {
                return IdentityClaimTypeConfig.ClaimType;
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in GetClaimTypeForUserKey", TraceCategory.Rehydration, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
            return null;
        }

        /// <summary>
        /// Return the user key (SPClaim with identity claim type) from the incoming entity
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        protected override SPClaim GetUserKeyForEntity(SPClaim entity)
        {
            if (!Initialize(null, null))
                return null;

            // There are 2 scenarios:
            // 1: OriginalIssuer is "SecurityTokenService": Value looks like "05.t|yvanhost|yvand@yvanhost.local", claim type is "http://schemas.microsoft.com/sharepoint/2009/08/claims/userid" and it must be decoded properly
            // 2: OriginalIssuer is AzureCP: in this case incoming entity is valid and returned as is
            if (String.Equals(entity.OriginalIssuer, IssuerName, StringComparison.InvariantCultureIgnoreCase))
                return entity;

            SPClaimProviderManager cpm = SPClaimProviderManager.Local;
            SPClaim curUser = SPClaimProviderManager.DecodeUserIdentifierClaim(entity);

            this.Lock_Config.EnterReadLock();
            try
            {
                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Returning user key for '{entity.Value}'",
                    TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Rehydration);
                return CreateClaim(IdentityClaimTypeConfig.ClaimType, curUser.Value, curUser.ValueType);
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in GetUserKeyForEntity", TraceCategory.Rehydration, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
            return null;
        }
    }

    public class AzureADResult
    {
        public List<DirectoryObject> UserOrGroupResultList;
        public List<string> DomainsRegisteredInAzureADTenant;
        //public string TenantName;

        public AzureADResult()
        {
            UserOrGroupResultList = new List<DirectoryObject>();
            DomainsRegisteredInAzureADTenant = new List<string>();
            //this.TenantName = tenantName;
        }
    }

    /// <summary>
    /// User / group found in Azure AD, with additional information
    /// </summary>
    public class AzureCPResult
    {
        public DirectoryObject UserOrGroupResult;
        public ClaimTypeConfig ClaimTypeConfig;
        public PickerEntity PickerEntity;
        public string PermissionValue;
        public string QueryMatchValue;
        //public string TenantName;

        public AzureCPResult(DirectoryObject directoryObject)
        {
            UserOrGroupResult = directoryObject;
            //TenantName = tenantName;
        }
    }
}
