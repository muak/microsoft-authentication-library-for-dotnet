﻿//----------------------------------------------------------------------
//
// Copyright (c) Microsoft Corporation.
// All rights reserved.
//
// This code is licensed under the MIT License.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
//------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Microsoft.IdentityModel.Clients.ActiveDirectory
{
    internal class AcquireTokenInteractiveHandler : AcquireTokenHandlerBase
    {
        internal AuthorizationResult authorizationResult;

        private readonly Uri redirectUri;

        private readonly string redirectUriRequestParameter;

        private readonly IPlatformParameters authorizationParameters;

        private readonly string extraQueryParameters;

        private readonly string claims;

        private readonly IWebUI webUi;

        private readonly UserIdentifier userId;

        public AcquireTokenInteractiveHandler(RequestData requestData, Uri redirectUri, IPlatformParameters parameters, UserIdentifier userId, string extraQueryParameters, IWebUI webUI, string claims = "")
            : base(requestData)
        {
            this.redirectUri = PlatformPlugin.PlatformInformation.ValidateRedirectUri(redirectUri, this.CallState);

            if (!string.IsNullOrWhiteSpace(this.redirectUri.Fragment))
            {
                throw new ArgumentException(AdalErrorMessage.RedirectUriContainsFragment, "redirectUri");
            }

            this.authorizationParameters = parameters;

            this.redirectUriRequestParameter = PlatformPlugin.PlatformInformation.GetRedirectUriAsString(this.redirectUri, this.CallState);

            if (userId == null)
            {
                throw new ArgumentNullException("userId", AdalErrorMessage.SpecifyAnyUser);
            }

            this.userId = userId;

            if (!string.IsNullOrEmpty(extraQueryParameters) && extraQueryParameters[0] == '&')
            {
                extraQueryParameters = extraQueryParameters.Substring(1);
            }

            this.extraQueryParameters = extraQueryParameters;
            this.webUi = webUI;
            this.UniqueId = userId.UniqueId;
            this.DisplayableId = userId.DisplayableId;
            this.UserIdentifierType = userId.Type;
            this.SupportADFS = true;

            claims = ProcessClaims(extraQueryParameters, claims);
            if (!String.IsNullOrEmpty(claims))
            {
                PlatformPlugin.Logger.Verbose(CallState,
                string.Format(CultureInfo.InvariantCulture,
                    "Claims present. Skip cache lookup."));
            }
            this.LoadFromCache = (requestData.TokenCache != null && parameters != null && PlatformPlugin.PlatformInformation.GetCacheLoadPolicy(parameters) && String.IsNullOrEmpty(claims));

            // Push claims into extraQueryParameters.
            if (!String.IsNullOrEmpty(this.extraQueryParameters))
            {
                this.extraQueryParameters += "&";
            }
            this.extraQueryParameters += "claims=" + claims;

            this.brokerParameters["force"] = "NO";
            if (userId != UserIdentifier.AnyUser)
            {
                this.brokerParameters["username"] = userId.Id;
            }
            else
            {
                this.brokerParameters["username"] = string.Empty;
            }
            this.brokerParameters["username_type"] = userId.Type.ToString();

            this.brokerParameters["redirect_uri"] = this.redirectUri.AbsoluteUri;
            this.brokerParameters["extra_qp"] = extraQueryParameters;
            this.brokerParameters["claims"] = claims;
            PlatformPlugin.BrokerHelper.PlatformParameters = authorizationParameters;
        }

        protected override async Task PreTokenRequest()
        {
            await base.PreTokenRequest().ConfigureAwait(false);

            // We do not have async interactive API in .NET, so we call this synchronous method instead.
            await this.AcquireAuthorizationAsync().ConfigureAwait(false);
            this.VerifyAuthorizationResult();
        }

        internal async Task AcquireAuthorizationAsync()
        {
            Uri authorizationUri = this.CreateAuthorizationUri();
            this.authorizationResult = await this.webUi.AcquireAuthorizationAsync(authorizationUri, this.redirectUri, this.CallState).ConfigureAwait(false);
        }

        internal async Task<Uri> CreateAuthorizationUriAsync(Guid correlationId)
        {
            this.CallState.CorrelationId = correlationId;
            await this.Authenticator.UpdateFromTemplateAsync(this.CallState).ConfigureAwait(false);
            return this.CreateAuthorizationUri();
        }
        protected override void AddAditionalRequestParameters(DictionaryRequestParameters requestParameters)
        {
            requestParameters[OAuthParameter.GrantType] = OAuthGrantType.AuthorizationCode;
            requestParameters[OAuthParameter.Code] = this.authorizationResult.Code;
            requestParameters[OAuthParameter.RedirectUri] = this.redirectUriRequestParameter;
        }

        protected override void PostTokenRequest(AuthenticationResultEx resultEx)
        {
            base.PostTokenRequest(resultEx);
            if ((this.DisplayableId == null && this.UniqueId == null) || this.UserIdentifierType == UserIdentifierType.OptionalDisplayableId)
            {
                return;
            }

            string uniqueId = (resultEx.Result.UserInfo != null && resultEx.Result.UserInfo.UniqueId != null) ? resultEx.Result.UserInfo.UniqueId : "NULL";
            string displayableId = (resultEx.Result.UserInfo != null) ? resultEx.Result.UserInfo.DisplayableId : "NULL";

            if (this.UserIdentifierType == UserIdentifierType.UniqueId && string.Compare(uniqueId, this.UniqueId, StringComparison.Ordinal) != 0)
            {
                throw new AdalUserMismatchException(this.UniqueId, uniqueId);
            }

            if (this.UserIdentifierType == UserIdentifierType.RequiredDisplayableId && string.Compare(displayableId, this.DisplayableId, StringComparison.OrdinalIgnoreCase) != 0)
            {
                throw new AdalUserMismatchException(this.DisplayableId, displayableId);
            }
        }

        private Uri CreateAuthorizationUri()
        {
            string loginHint = null;

            if (!userId.IsAnyUser
                && (userId.Type == UserIdentifierType.OptionalDisplayableId
                    || userId.Type == UserIdentifierType.RequiredDisplayableId))
            {
                loginHint = userId.Id;
            }

            IRequestParameters requestParameters = this.CreateAuthorizationRequest(loginHint);

            return new Uri(new Uri(this.Authenticator.AuthorizationUri), "?" + requestParameters);
        }

        private DictionaryRequestParameters CreateAuthorizationRequest(string loginHint)
        {
            var authorizationRequestParameters = new DictionaryRequestParameters(this.Resource, this.ClientKey);
            authorizationRequestParameters[OAuthParameter.ResponseType] = OAuthResponseType.Code;
            authorizationRequestParameters[OAuthParameter.HasChrome] = "1";
            authorizationRequestParameters[OAuthParameter.RedirectUri] = this.redirectUriRequestParameter;

            if (!string.IsNullOrWhiteSpace(loginHint))
            {
                authorizationRequestParameters[OAuthParameter.LoginHint] = loginHint;
            }

            if (this.CallState != null && this.CallState.CorrelationId != Guid.Empty)
            {
                authorizationRequestParameters[OAuthParameter.CorrelationId] = this.CallState.CorrelationId.ToString();
            }

            if (this.authorizationParameters != null)
            {
                PlatformPlugin.PlatformInformation.AddPromptBehaviorQueryParameter(this.authorizationParameters, authorizationRequestParameters);
            }

            if (PlatformPlugin.HttpClientFactory.AddAdditionalHeaders)
            {
                IDictionary<string, string> adalIdParameters = AdalIdHelper.GetAdalIdParameters();
                foreach (KeyValuePair<string, string> kvp in adalIdParameters)
                {
                    authorizationRequestParameters[kvp.Key] = kvp.Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(extraQueryParameters))
            {
                // Checks for extraQueryParameters duplicating standard parameters
                Dictionary<string, string> kvps = EncodingHelper.ParseKeyValueList(extraQueryParameters, '&', false, this.CallState);
                foreach (KeyValuePair<string, string> kvp in kvps)
                {
                    if (authorizationRequestParameters.ContainsKey(kvp.Key))
                    {
                        throw new AdalException(AdalError.DuplicateQueryParameter, string.Format(CultureInfo.CurrentCulture, AdalErrorMessage.DuplicateQueryParameterTemplate, kvp.Key));
                    }
                }

                authorizationRequestParameters.ExtraQueryParameter = extraQueryParameters;
            }

            return authorizationRequestParameters;
        }

        private void VerifyAuthorizationResult()
        {
            if (this.authorizationResult.Error == OAuthError.LoginRequired)
            {
                throw new AdalException(AdalError.UserInteractionRequired);
            }

            if (this.authorizationResult.Status != AuthorizationStatus.Success)
            {
                throw new AdalServiceException(this.authorizationResult.Error, this.authorizationResult.ErrorDescription);
            }
        }


        protected override void UpdateBrokerParameters(IDictionary<string, string> parameters)
        {
            Uri uri = new Uri(this.authorizationResult.Code);
            string query = EncodingHelper.UrlDecode(uri.Query);
            Dictionary<string, string> kvps = EncodingHelper.ParseKeyValueList(query, '&', false, this.CallState);
            parameters["username"] = kvps["username"];
        }

        protected override bool BrokerInvocationRequired()
        {
            if (this.authorizationResult != null
                && !string.IsNullOrEmpty(this.authorizationResult.Code)
                && this.authorizationResult.Code.StartsWith("msauth://", StringComparison.CurrentCultureIgnoreCase))
            {
                this.brokerParameters["broker_install_url"] = this.authorizationResult.Code;
                return true;
            }

            return false;
        }

        private string ProcessClaims(string extraQueryParameters, string claims)
        {
            // Only process the extra query parameters if it's not null.
            if (string.IsNullOrEmpty(extraQueryParameters))
            {
                return claims;
            }

            // Split the query parameters on the ampersand.
            string[] parts = extraQueryParameters.Split('&');
            foreach (string part in parts)
            {
                // Split the key and value on equal sign.
                string[] nameValue = part.Split('=');
                if (nameValue.Length > 2)
                {
                    // If there are multiple equal signs, then query paramerter is not well formed, so skip
                    // Example of poorly formed query parameter string: var=12=45=67
                    continue;
                }

                // Don't process anything but claims query parameter
                if (!nameValue[0].Equals("claims"))
                {
                    continue;
                }

                string qpClaims = nameValue[1];

                // Now make sure they match; otherwise throw an error.
                if (!string.IsNullOrEmpty(qpClaims) && String.IsNullOrEmpty(claims)
                    && String.Compare(claims, qpClaims, StringComparison.CurrentCultureIgnoreCase) == 0)
                {
                    throw new ArgumentException("The claims parameter must match the claims in the extra query parameter.");
                }

                if (!string.IsNullOrEmpty(qpClaims))
                {
                    return qpClaims;
                }
            }

            // If there are query parameters, but none of them are the claims parameter, then return the claims string
            return claims;
        }
    }
}