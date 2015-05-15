﻿using System;
using System.Data.Entity;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using Microsoft.IdentityModel.Protocols;
using Microsoft.Owin;
using Microsoft.Owin.Security;
using Mvc.Server.Extensions;
using Mvc.Server.Models;
using Owin;
using Owin.Security.OpenIdConnect.Extensions;
using Owin.Security.OpenIdConnect.Server;

namespace Mvc.Server.Controllers {
    public class AuthorizationController : Controller {
        [AcceptVerbs(HttpVerbs.Get | HttpVerbs.Post)]
        [Route("~/connect/authorize", Order = 1)]
        public async Task<ActionResult> Authorize(CancellationToken cancellationToken) {
            // Note: this action is bound to the AuthorizationEndpointPath defined in Startup.cs
            // (by default "/connect/authorize" if you don't specify an explicit path).
            // When an OpenID Connect request arrives, it is automatically inspected by
            // OpenIdConnectServerHandler before this action is executed by ASP.NET MVC.
            // It is the only endpoint the OpenID Connect request can be extracted from.
            // For the rest of the authorization process, it will be stored in the user's session and retrieved
            // using "Context.Session.GetOpenIdConnectRequest" instead of "Context.GetOpenIdConnectRequest",
            // that would otherwise extract the OpenID Connect request from the query string or from the request body.

            // Note: when a fatal error occurs during the request processing, an OpenID Connect response
            // is prematurely forged and added to the OWIN context by OpenIdConnectServerHandler.
            // In this case, the OpenID Connect request is null and cannot be used.
            // When the user agent can be safely redirected to the client application,
            // OpenIdConnectServerHandler automatically handles the error and MVC is not invoked.
            // You can safely remove this part and let Owin.Security.OpenIdConnect.Server automatically
            // handle the unrecoverable errors by switching ApplicationCanDisplayErrors to false in Startup.cs
            var response = OwinContext.GetOpenIdConnectResponse();
            if (response != null) {
                return View("Error", response);
            }

            // Extract the authorization request from the OWIN environment.
            var request = OwinContext.GetOpenIdConnectRequest();
            if (request == null) {
                return View("Error", new OpenIdConnectMessage {
                    Error = "invalid_request",
                    ErrorDescription = "An internal error has occurred"
                });
            }

            // Generate a unique 16-bytes identifier and save
            // the OpenID Connect request in the user's session.
            var key = GenerateKey();
            Session.SetOpenIdConnectRequest(key, request);

            // Note: authentication could be theorically enforced at the filter level via AuthorizeAttribute
            // but this authorization endpoint accepts both GET and POST requests while the cookie middleware
            // only uses 302 responses to redirect the user agent to the login page, making it incompatible with POST.
            // To work around this limitation, the OpenID Connect request is saved in the user's session and will
            // be restored in the other "Authorize" method, after the authentication process has been completed.
            if (User.Identity == null || !User.Identity.IsAuthenticated) {
                OwinContext.Authentication.Challenge(new AuthenticationProperties {
                    RedirectUri = Url.Action("Authorize", new { key })
                });

                return new HttpStatusCodeResult(401);
            }

            // Note: Owin.Security.OpenIdConnect.Server automatically ensures an application
            // corresponds to the client_id specified in the authorization request using
            // IOpenIdConnectServerProvider.ValidateClientRedirectUri (see AuthorizationProvider.cs).
            // In theory, this null check is thus not strictly necessary. That said, a race condition
            // and a null reference exception could appear here if you manually removed the application
            // details from the database after the initial check made by Owin.Security.OpenIdConnect.Server.
            var application = await GetApplicationAsync(request.ClientId, cancellationToken);
            if (application == null) {
                return View("Error", new OpenIdConnectMessage {
                    Error = "invalid_client",
                    ErrorDescription = "Details concerning the calling client application cannot be found in the database"
                });
            }

            // Note: in a real world application, you'd probably prefer creating a specific view model.
            return View("Authorize", Tuple.Create(request, application, key));
        }

        [Authorize, HttpGet, Route("~/connect/authorize/{key}", Order = 0)]
        public async Task<ActionResult> Authorize(string key, CancellationToken cancellationToken) {
            // Extract the OpenID Connect request stored in the user's session.
            var request = Session.GetOpenIdConnectRequest(key);
            if (request == null) {
                return View("Error", new OpenIdConnectMessage {
                    Error = "invalid_request",
                    ErrorDescription = "An internal error has occurred"
                });
            }

            // Note: Owin.Security.OpenIdConnect.Server automatically ensures an application
            // corresponds to the client_id specified in the authorization request using
            // IOpenIdConnectServerProvider.ValidateClientRedirectUri (see AuthorizationProvider.cs).
            // In theory, this null check is thus not strictly necessary. That said, a race condition
            // and a null reference exception could appear here if you manually removed the application
            // details from the database after the initial check made by Owin.Security.OpenIdConnect.Server.
            var application = await GetApplicationAsync(request.ClientId, cancellationToken);
            if (application == null) {
                return View("Error", new OpenIdConnectMessage {
                    Error = "invalid_client",
                    ErrorDescription = "Details concerning the calling client application cannot be found in the database"
                });
            }

            // Note: in a real world application, you'd probably prefer creating a specific view model.
            return View("Authorize", Tuple.Create(request, application, key));
        }

        [Authorize, HttpPost, Route("~/connect/authorize/accept/{key}"), ValidateAntiForgeryToken]
        public async Task<ActionResult> Accept(string key, CancellationToken cancellationToken) {
            // Extract the OpenID Connect request stored in the user's session.
            var request = Session.GetOpenIdConnectRequest(key);
            if (request == null) {
                return View("Error", new OpenIdConnectMessage {
                    Error = "invalid_request",
                    ErrorDescription = "An internal error has occurred"
                });
            }

            // Restore the OpenID Connect request in the OWIN context
            // so Owin.Security.OpenIdConnect.Server can retrieve it.
            OwinContext.SetOpenIdConnectRequest(request);

            // Remove the OpenID Connect request stored in the user's session.
            Session.SetOpenIdConnectRequest(key, null);

            // Create a new ClaimsIdentity containing the claims that
            // will be used to create an id_token, a token or a code.
            var identity = new ClaimsIdentity(OpenIdConnectDefaults.AuthenticationType);

            foreach (var claim in OwinContext.Authentication.User.Claims) {
                // Allow ClaimTypes.Name to be added in the id_token.
                // ClaimTypes.NameIdentifier is automatically added, even if its
                // destination is not defined or doesn't include "id_token".
                // The other claims won't be visible for the client application.
                if (claim.Type == ClaimTypes.Name) {
                    claim.WithDestination("id_token")
                         .WithDestination("token");
                }

                identity.AddClaim(claim);
            }

            // Note: Owin.Security.OpenIdConnect.Server automatically ensures an application
            // corresponds to the client_id specified in the authorization request using
            // IOpenIdConnectServerProvider.ValidateClientRedirectUri (see AuthorizationProvider.cs).
            // In theory, this null check is thus not strictly necessary. That said, a race condition
            // and a null reference exception could appear here if you manually removed the application
            // details from the database after the initial check made by Owin.Security.OpenIdConnect.Server.
            var application = await GetApplicationAsync(request.ClientId, CancellationToken.None);
            if (application == null) {
                return View("Error", new OpenIdConnectMessage {
                    Error = "invalid_client",
                    ErrorDescription = "Details concerning the calling client application cannot be found in the database"
                });
            }

            // Create a new ClaimsIdentity containing the claims associated with the application.
            // Note: setting identity.Actor is not mandatory but can be useful to access
            // the whole delegation chain from the resource server (see ResourceController.cs).
            identity.Actor = new ClaimsIdentity(OpenIdConnectDefaults.AuthenticationType);
            identity.Actor.AddClaim(ClaimTypes.NameIdentifier, application.ApplicationID);
            identity.Actor.AddClaim(ClaimTypes.Name, application.DisplayName, destination: "id_token token");

            // This call will instruct Owin.Security.OpenIdConnect.Server to serialize
            // the specified identity to build appropriate tokens (id_token and token).
            // Note: you should always make sure the identities you return contain either
            // a 'sub' or a 'ClaimTypes.NameIdentifier' claim. In this case, the returned
            // identities always contain the name identifier returned by the external provider.
            OwinContext.Authentication.SignIn(identity);

            return new HttpStatusCodeResult(200);
        }

        [Authorize, HttpPost, Route("~/connect/authorize/deny/{key}"), ValidateAntiForgeryToken]
        public ActionResult Deny(string key, CancellationToken cancellationToken) {
            // Extract the OpenID Connect request stored in the user's session.
            var request = Session.GetOpenIdConnectRequest(key);
            if (request == null) {
                return View("Error", new OpenIdConnectMessage {
                    Error = "invalid_request",
                    ErrorDescription = "An internal error has occurred"
                });
            }

            // Restore the OpenID Connect request in the OWIN context
            // so Owin.Security.OpenIdConnect.Server can retrieve it.
            OwinContext.SetOpenIdConnectRequest(request);

            // Remove the OpenID Connect request stored in the user's session.
            Session.SetOpenIdConnectRequest(key, null);

            // Notify Owin.Security.OpenIdConnect.Server that the authorization grant has been denied.
            // Note: OpenIdConnectServerHandler will automatically take care of redirecting
            // the user agent to the client application using the appropriate response_mode.
            OwinContext.SetOpenIdConnectResponse(new OpenIdConnectMessage {
                Error = "access_denied",
                ErrorDescription = "The authorization grant has been denied by the resource owner",
                RedirectUri = request.RedirectUri,
                State = request.State
            });

            return new HttpStatusCodeResult(200);
        }

        [HttpGet, Route("~/connect/logout")]
        public async Task<ActionResult> Logout() {
            // Note: when a fatal error occurs during the request processing, an OpenID Connect response
            // is prematurely forged and added to the OWIN context by OpenIdConnectServerHandler.
            // In this case, the OpenID Connect request is null and cannot be used.
            // When the user agent can be safely redirected to the client application,
            // OpenIdConnectServerHandler automatically handles the error and MVC is not invoked.
            // You can safely remove this part and let Owin.Security.OpenIdConnect.Server automatically
            // handle the unrecoverable errors by switching ApplicationCanDisplayErrors to false in Startup.cs
            var response = OwinContext.GetOpenIdConnectResponse();
            if (response != null) {
                return View("Error", response);
            }

            // When invoked, the logout endpoint might receive an unauthenticated request if the server cookie has expired.
            // When the client application sends an id_token_hint parameter, the corresponding identity can be retrieved
            // using AuthenticateAsync or using User when the authorization server is declared as AuthenticationMode.Active.
            var identity = await OwinContext.Authentication.AuthenticateAsync(OpenIdConnectDefaults.AuthenticationType);

            // Extract the logout request from the OWIN environment.
            var request = OwinContext.GetOpenIdConnectRequest();
            if (request == null) {
                return View("Error", new OpenIdConnectMessage {
                    Error = "invalid_request",
                    ErrorDescription = "An internal error has occurred"
                });
            }

            return View("Logout", Tuple.Create(request, identity));
        }

        [HttpPost, Route("~/connect/logout")]
        [ValidateAntiForgeryToken]
        public ActionResult Logout(CancellationToken cancellationToken) {
            // Instruct the cookies middleware to delete the local cookie created
            // when the user agent is redirected from the external identity provider
            // after a successful authentication flow (e.g Google or Facebook).
            OwinContext.Authentication.SignOut("ServerCookie");

            // This call will instruct Owin.Security.OpenIdConnect.Server to serialize
            // the specified identity to build appropriate tokens (id_token and token).
            // Note: you should always make sure the identities you return contain either
            // a 'sub' or a 'ClaimTypes.NameIdentifier' claim. In this case, the returned
            // identities always contain the name identifier returned by the external provider.
            OwinContext.Authentication.SignOut(OpenIdConnectDefaults.AuthenticationType);

            return new HttpStatusCodeResult(200);
        }
        
        /// <summary>
        /// Gets the IOwinContext instance associated with the current request.
        /// </summary>
        protected IOwinContext OwinContext {
            get {
                var context = HttpContext.GetOwinContext();
                if (context == null) {
                    throw new NotSupportedException("An OWIN context cannot be extracted from HttpContext");
                }

                return context;
            }
        }

        protected virtual async Task<Application> GetApplicationAsync(string identifier, CancellationToken cancellationToken) {
            using (var context = new ApplicationContext()) {
                // Retrieve the application details corresponding to the requested client_id.
                return await (from application in context.Applications
                              where application.ApplicationID == identifier
                              select application).SingleOrDefaultAsync(cancellationToken);
            }
        }

        protected virtual string GenerateKey() {
            using (var generator = RandomNumberGenerator.Create()) {
                var buffer = new byte[16];
                generator.GetBytes(buffer);

                return new Guid(buffer).ToString();
            }
        }
    }
}