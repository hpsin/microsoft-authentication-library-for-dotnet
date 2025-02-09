﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Identity.Client.Internal.Requests;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Identity.Client.Instance;
using Microsoft.Identity.Client.Internal;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.TelemetryCore;
using System.Threading;
using Microsoft.Identity.Client.ApiConfig;
using Microsoft.Identity.Client.ApiConfig.Parameters;
using Microsoft.Identity.Client.Http;
using Microsoft.Identity.Client.ApiConfig.Executors;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Identity.Client.Cache.CacheImpl;

namespace Microsoft.Identity.Client
{
    /// <summary>
    /// Class to be used for confidential client applications (web apps, web APIs, and daemon applications).
    /// </summary>
    /// <remarks>
    /// Confidential client applications are typically applications which run on servers (web apps, web API, or even service/daemon applications).
    /// They are considered difficult to access, and therefore capable of keeping an application secret (hold configuration
    /// time secrets as these values would be difficult for end users to extract).
    /// A web app is the most common confidential client. The clientId is exposed through the web browser, but the secret is passed only in the back channel
    /// and never directly exposed. For details see https://aka.ms/msal-net-client-applications
    /// </remarks>
#if !SUPPORTS_CONFIDENTIAL_CLIENT
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]  // hide confidentail client on mobile
#endif
    public sealed partial class ConfidentialClientApplication
        : ClientApplicationBase,
            IConfidentialClientApplication,
            IConfidentialClientApplicationWithCertificate,
            IByRefreshToken
    {
        /// <summary>
        /// Instructs MSAL to try to auto discover the Azure region.
        /// </summary>
        public const string AttemptRegionDiscovery = "TryAutoDetect";


        internal ConfidentialClientApplication(
            ApplicationConfiguration configuration)
            : base(configuration)
        {
            GuardMobileFrameworks();

            InMemoryPartitionedCacheSerializer = new InMemoryPartitionedCacheSerializer(ServiceBundle.ApplicationLogger);
            AppTokenCacheInternal = configuration.AppTokenCacheInternalForTest ?? 
                new TokenCache(ServiceBundle, true, InMemoryPartitionedCacheSerializer);
            Certificate = configuration.ClientCredentialCertificate;

            this.ServiceBundle.ApplicationLogger.Verbose($"ConfidentialClientApplication {configuration.GetHashCode()} created");
        }

        /// <summary>
        /// Acquires a security token from the authority configured in the app using the authorization code
        /// previously received from the STS.
        /// It uses the OAuth 2.0 authorization code flow (See https://aka.ms/msal-net-authorization-code).
        /// It's usually used in web apps (for instance ASP.NET / ASP.NET Core web apps) which sign-in users,
        /// and can request an authorization code.
        /// This method does not lookup the token cache, but stores the result in it, so it can be looked up
        /// using other methods such as <see cref="IClientApplicationBase.AcquireTokenSilent(IEnumerable{string}, IAccount)"/>.
        /// </summary>
        /// <param name="scopes">Scopes requested to access a protected API</param>
        /// <param name="authorizationCode">The authorization code received from the service authorization endpoint.</param>
        /// <returns>A builder enabling you to add optional parameters before executing the token request</returns>
        /// <remarks>You can set optional parameters by chaining the builder with:
        /// <see cref="AbstractAcquireTokenParameterBuilder{T}.WithAuthority(string, bool)"/>,
        /// <see cref="AbstractAcquireTokenParameterBuilder{T}.WithExtraQueryParameters(Dictionary{string, string})"/>,
        /// </remarks>
        public AcquireTokenByAuthorizationCodeParameterBuilder AcquireTokenByAuthorizationCode(
            IEnumerable<string> scopes,
            string authorizationCode)
        {
            return AcquireTokenByAuthorizationCodeParameterBuilder.Create(
                ClientExecutorFactory.CreateConfidentialClientExecutor(this),
                scopes,
                authorizationCode);
        }

        /// <summary>
        /// Acquires a token from the authority configured in the app, for the confidential client itself (in the name of no user)
        /// using the client credentials flow. See https://aka.ms/msal-net-client-credentials.
        /// </summary>
        /// <param name="scopes">scopes requested to access a protected API. For this flow (client credentials), the scopes
        /// should be of the form "{ResourceIdUri/.default}" for instance <c>https://management.azure.net/.default</c> or, for Microsoft
        /// Graph, <c>https://graph.microsoft.com/.default</c> as the requested scopes are defined statically with the application registration
        /// in the portal, and cannot be overriden in the application.</param>
        /// <returns>A builder enabling you to add optional parameters before executing the token request</returns>
        /// <remarks>You can also chain the following optional parameters:
        /// <see cref="AcquireTokenForClientParameterBuilder.WithForceRefresh(bool)"/>
        /// <see cref="AbstractAcquireTokenParameterBuilder{T}.WithExtraQueryParameters(Dictionary{string, string})"/>
        /// </remarks>
        public AcquireTokenForClientParameterBuilder AcquireTokenForClient(
            IEnumerable<string> scopes)
        {
            return AcquireTokenForClientParameterBuilder.Create(
                ClientExecutorFactory.CreateConfidentialClientExecutor(this),
                scopes);
        }

        /// <summary>
        /// Acquires an access token for this application (usually a Web API) from the authority configured in the application,
        /// in order to access another downstream protected web API on behalf of a user using the OAuth 2.0 On-Behalf-Of flow.
        /// See https://aka.ms/msal-net-on-behalf-of.
        /// This confidential client application was itself called with a token which will be provided in the
        /// <paramref name="userAssertion">userAssertion</paramref> parameter.
        /// </summary>
        /// <param name="scopes">Scopes requested to access a protected API</param>
        /// <param name="userAssertion">Instance of <see cref="UserAssertion"/> containing credential information about
        /// the user on behalf of whom to get a token.</param>
        /// <returns>A builder enabling you to add optional parameters before executing the token request</returns>
        /// <remarks>You can also chain the following optional parameters:
        /// <see cref="AbstractAcquireTokenParameterBuilder{T}.WithExtraQueryParameters(Dictionary{string, string})"/>
        /// </remarks>
        public AcquireTokenOnBehalfOfParameterBuilder AcquireTokenOnBehalfOf(
            IEnumerable<string> scopes,
            UserAssertion userAssertion)
        {
            if (userAssertion == null)
            {
                ServiceBundle.ApplicationLogger.Error("User assertion for OBO request should not be null");
                throw new MsalClientException(MsalError.UserAssertionNullError);
            }

            return AcquireTokenOnBehalfOfParameterBuilder.Create(
                ClientExecutorFactory.CreateConfidentialClientExecutor(this),
                scopes,
                userAssertion);
        }

        /// <summary>
        /// Computes the URL of the authorization request letting the user sign-in and consent to the application accessing specific scopes in
        /// the user's name. The URL targets the /authorize endpoint of the authority configured in the application.
        /// This override enables you to specify a login hint and extra query parameter.
        /// </summary>
        /// <param name="scopes">Scopes requested to access a protected API</param>
        /// <returns>A builder enabling you to add optional parameters before executing the token request to get the
        /// URL of the STS authorization endpoint parametrized with the parameters</returns>
        /// <remarks>You can also chain the following optional parameters:
        /// <see cref="GetAuthorizationRequestUrlParameterBuilder.WithRedirectUri(string)"/>
        /// <see cref="GetAuthorizationRequestUrlParameterBuilder.WithLoginHint(string)"/>
        /// <see cref="AbstractAcquireTokenParameterBuilder{T}.WithExtraQueryParameters(Dictionary{string, string})"/>
        /// <see cref="GetAuthorizationRequestUrlParameterBuilder.WithExtraScopesToConsent(IEnumerable{string})"/>
        /// </remarks>
        public GetAuthorizationRequestUrlParameterBuilder GetAuthorizationRequestUrl(
            IEnumerable<string> scopes)
        {
            return GetAuthorizationRequestUrlParameterBuilder.Create(
                ClientExecutorFactory.CreateConfidentialClientExecutor(this),
                scopes);
        }

        AcquireTokenByRefreshTokenParameterBuilder IByRefreshToken.AcquireTokenByRefreshToken(
            IEnumerable<string> scopes,
            string refreshToken)
        {
            return AcquireTokenByRefreshTokenParameterBuilder.Create(
                ClientExecutorFactory.CreateClientApplicationBaseExecutor(this),
                scopes,
                refreshToken);
        }

        internal ClientCredentialWrapper ClientCredential => ServiceBundle.Config.ClientCredential;

        /// <Summary>
        /// Application token cache. This case holds access tokens and refresh tokens for the application. It's maintained
        /// and updated silently if needed when calling <see cref="AcquireTokenForClient(IEnumerable{string})"/>
        /// </Summary>
        /// <remarks>On .NET Framework and .NET Core you can also customize the token cache serialization.
        /// See https://aka.ms/msal-net-token-cache-serialization. This is taken care of by MSAL.NET on other platforms
        /// </remarks>
        public ITokenCache AppTokenCache => AppTokenCacheInternal;

        /// <summary>
        /// The certificate used to create this <see cref="ConfidentialClientApplication"/>, if any.
        /// </summary>
        public X509Certificate2 Certificate { get; }

        // Stores all app tokens
        internal ITokenCacheInternal AppTokenCacheInternal { get; }

        // App token cache is serialized by default (unless the user overrides this) 
        // the serialization stores tokens in this dictionary, where the key is the client_id + tenant_id
        // This makes cache operations be O(1) instead of O(n), and avoids catastrophic latency of
        // multi-tenant apps that do not serialize their cache.
        internal InMemoryPartitionedCacheSerializer InMemoryPartitionedCacheSerializer { get; }

        internal override async Task<AuthenticationRequestParameters> CreateRequestParametersAsync(
            AcquireTokenCommonParameters commonParameters,
            RequestContext requestContext,
            ITokenCacheInternal cache)
        {
            AuthenticationRequestParameters requestParams = await base.CreateRequestParametersAsync(commonParameters, requestContext, cache).ConfigureAwait(false);
            return requestParams;
        }

        internal static void GuardMobileFrameworks()
        {
#if ANDROID || iOS || WINDOWS_APP || MAC
            throw new PlatformNotSupportedException(
                "Confidential Client flows are not available on mobile platforms or on Mac." +
                "See https://aka.ms/msal-net-confidential-availability for details.");
#endif
        }
    }
}
