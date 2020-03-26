using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Autodesk.Forge;
using Autodesk.Forge.Client;
using Autodesk.Forge.Core;
using Autodesk.Forge.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SalesDemoToolApp.Utilities;

namespace IoConfigDemo
{
    /// <summary>
    /// Class to work with Forge APIs.
    /// </summary>
    class Forge : IForge
    {
        private readonly ILogger<Forge> _logger;
        private static readonly Scope[] _scope = { Scope.DataRead, Scope.BucketCreate, Scope.BucketRead };

        // Initialize the 2-legged oAuth 2.0 client.
        private static readonly TwoLeggedApi _twoLeggedApi = new TwoLeggedApi();

        private string _twoLeggedAccessToken;

        /// <summary>
        /// Forge configuration.
        /// </summary>
        public ForgeConfiguration Configuration { get; }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="optionsAccessor"></param>
        public Forge(IOptionsMonitor<ForgeConfiguration> optionsAccessor, ILogger<Forge> logger)
        {
            _logger = logger;
            Configuration = optionsAccessor.CurrentValue.Validate();
        }

        private async Task<string> GetTwoLeggedAccessToken()
        {
            if (_twoLeggedAccessToken == null)
            {
                dynamic bearer = await _2leggedAsync();
                _twoLeggedAccessToken = bearer.access_token;
            }

            return _twoLeggedAccessToken;
        }

        private async Task<dynamic> _2leggedAsync()
        {
            // Call the asynchronous version of the 2-legged client with HTTP information
            // HTTP information helps to verify if the call was successful as well as read the HTTP transaction headers.
            Autodesk.Forge.Client.ApiResponse<dynamic> response = await _twoLeggedApi.AuthenticateAsyncWithHttpInfo(Configuration.ClientId, Configuration.ClientSecret, oAuthConstants.CLIENT_CREDENTIALS, _scope);

            if (response.StatusCode != StatusCodes.Status200OK)
            {
                throw new Exception("Request failed! (with HTTP response " + response.StatusCode + ")");
            }

            // The JSON response from the oAuth server is the Data variable and has already been parsed into a DynamicDictionary object.
            return response.Data;
        }

        public async Task<List<ObjectDetails>> GetBucketObjects(string bucketKey)
        {
            await EnsureBucket(bucketKey);

            ObjectsApi objectsApi = new ObjectsApi();
            objectsApi.Configuration.AccessToken = await GetTwoLeggedAccessToken();
            var objects = new List<ObjectDetails>();

            dynamic objectsList = await objectsApi.GetObjectsAsync(bucketKey);
            foreach (KeyValuePair<string, dynamic> objInfo in new DynamicDictionaryItems(objectsList.items))
            {
                var details = new ObjectDetails
                {
                    BucketKey = objInfo.Value.bucketKey,
                    ObjectId = objInfo.Value.objectId,
                    ObjectKey = objInfo.Value.objectKey,
                    Sha1 = System.Text.Encoding.ASCII.GetBytes(objInfo.Value.sha1),
                    Size = (int?)objInfo.Value.size,
                    Location = objInfo.Value.location
                };
                objects.Add(details);
            }

            return objects;
        }

        /// <summary>
        /// Make sure the bucket is exists.  Create if necessary.
        /// </summary>
        /// <param name="bucketName">The bucket name.</param>
        private async Task EnsureBucket(string bucketName)
        {
            var api = new BucketsApi { Configuration = { AccessToken = await GetTwoLeggedAccessToken() }};

            try
            {
                var payload = new PostBucketsPayload(bucketName, /*allow*/null, PostBucketsPayload.PolicyKeyEnum.Persistent);
                await api.CreateBucketAsync(payload, /* use default (US region) */ null);
            }
            catch (ApiException e) when (e.ErrorCode == StatusCodes.Status409Conflict)
            {
                // swallow exception about "Conflict", which means the bucket exists already
            }
        }
    }
}