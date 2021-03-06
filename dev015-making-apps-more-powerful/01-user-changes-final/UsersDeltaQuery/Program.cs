﻿/*
  The MIT License (MIT)

Copyright (c) 2015 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using Microsoft.Graph;
using Microsoft.Identity.Client;
using System;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace UsersDeltaQuery
{
    class Program
    {
        static void Main(String[] args)
        {
            RunAsync(args).GetAwaiter().GetResult();
            Console.WriteLine("Press any key to finish.");
            Console.ReadKey();
        }

        static async Task RunAsync(String[] args)
        {
            var tenantId = Config.GetConfig("tenantId");

            ConfidentialClientApplication daemonClient = new ConfidentialClientApplication(
                Config.GetConfig("clientId"),
                String.Format(Config.GetConfig("authorityFormat"), tenantId),
                Config.GetConfig("replyUri"),
                new ClientCredential(Config.GetConfig("clientSecret")),
                null,
                new TokenCache());


            GraphServiceClient graphClient = new GraphServiceClient(
                "https://graph.microsoft.com/v1.0",
                new DelegateAuthenticationProvider(
                    async (requestMessage) =>
                    {
                        var authenticationResult = await daemonClient.AcquireTokenForClientAsync(new string[] { "https://graph.microsoft.com/.default" });
                        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("bearer", authenticationResult.AccessToken);
                    }));

        #region first sync of users

            Console.WriteLine("=== Getting users");

            /// <summary>
            /// Get the list of users in the tenant - first full sync using delta query
            /// It selects only the displayName and userPrincipalName
            /// This first call ONLY gets the first page of results
            /// Subsequent calls page through the results, until we reach the end, and get the delta link/token
            /// The delta token can then be used to get changes since the last time we called
            /// </summary>
            var userPage = await graphClient.Users
                .Delta()
                .Request()
                .Select("displayName,userPrincipalName")
                .GetAsync();

            /// <summary>
            /// Display users (by paging through the results) and get the delta link
            /// We'll use this same method later to get changes
            /// </summary>
            var deltaLink = await DisplayChangedUsersAndGetDeltaLink(userPage);

        #endregion


        #region adding a new user to test delta changes

            Console.WriteLine("=== Adding user");

            /// <summary>
            /// Create a new user
            /// </summary>
            var u = new User()
            {
                DisplayName = "UsersDeltaQuery Demo User",
                GivenName = "UsersDeltaQueryDemo",
                Surname = "User",
                MailNickname = "UsersDeltaQueryDemoUser",
                UserPrincipalName = Guid.NewGuid().ToString() + "@" + tenantId,
                PasswordProfile = new PasswordProfile() { ForceChangePasswordNextSignIn = true, Password = "D3m0p@55w0rd!" },
                AccountEnabled = true
            };
            var newUser = await graphClient.Users.Request().AddAsync(u);

        #endregion

        #region now get changes since last delta sync

            Console.WriteLine("Press any key to execute delta query.");
            Console.ReadKey();
            Console.WriteLine("=== Getting delta users");

            /// <summary>
            /// Get the first page using the delta link (to see the new user)
            /// </summary>
            userPage.InitializeNextPageRequest(graphClient, deltaLink);
            userPage = await userPage.NextPageRequest.GetAsync();

            /// <summary>
            /// Display users again and get NEW delta link... notice that only the added user is returned
            /// Keep trying (in case there are replication delays) to get changes
            /// </summary>
            var newDeltaLink = await DisplayChangedUsersAndGetDeltaLink(userPage);
            while (deltaLink.Equals(newDeltaLink))
            {
                // If the two are equal, then we didn't receive changes yet
                // Query to get first page using the delta link
                userPage.InitializeNextPageRequest(graphClient, deltaLink);
                userPage = await userPage.NextPageRequest.GetAsync();
                newDeltaLink = await DisplayChangedUsersAndGetDeltaLink(userPage);
            }

        #endregion

        #region clean-up
            Console.WriteLine("=== Deleting user");
            //Finally, delete the user
            await graphClient.Users[newUser.Id].Request().DeleteAsync();
        #endregion

        }

        static async Task<string> DisplayChangedUsersAndGetDeltaLink(IUserDeltaCollectionPage userPage)
        {
            /// <summary>
            /// Using the first page as a starting point (as the input)
            /// iterate through the first and subsequent pages, writing out the users in each page
            /// until you reach the last page (NextPageRequest is null)
            /// finally set the delta link by looking in the additional data, and return it
            /// </summary>

            /// Iterate through the users
            foreach (var user in userPage)
            {
                if (user.UserPrincipalName != null)
                    Console.WriteLine(user.UserPrincipalName.ToLower() + "\t\t" + user.DisplayName);
            }

            if (userPage.NextPageRequest != null)
            {
                userPage = await userPage.NextPageRequest.GetAsync();
                return await DisplayChangedUsersAndGetDeltaLink(userPage);
            }
            else
            {
                return (string)userPage.AdditionalData["@odata.deltaLink"];    
            }
        }
    }
}
