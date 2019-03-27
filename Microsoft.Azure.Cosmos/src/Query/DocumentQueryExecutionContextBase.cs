﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Query
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Cosmos.Collections;
    using Microsoft.Azure.Cosmos.Common;
    using Microsoft.Azure.Cosmos.CosmosElements;
    using Microsoft.Azure.Cosmos.Internal;
    using Microsoft.Azure.Cosmos.Json;
    using Microsoft.Azure.Cosmos.Linq;
    using Microsoft.Azure.Cosmos.Routing;
    using Newtonsoft.Json;

    internal abstract class DocumentQueryExecutionContextBase : IDocumentQueryExecutionContext
    {
        public struct InitParams
        {
            public IDocumentQueryClient Client { get; }
            public ResourceType ResourceTypeEnum { get; }
            public Type ResourceType { get; }
            public Expression Expression { get; }
            public CosmosQueryRequestOptions RequestOptions { get; }
            public string ResourceLink { get; }
            public bool GetLazyFeedResponse { get; }
            public Guid CorrelatedActivityId { get; }

            public InitParams(
                IDocumentQueryClient client,
                ResourceType resourceTypeEnum,
                Type resourceType,
                Expression expression,
                CosmosQueryRequestOptions queryRequestOptions,
                string resourceLink,
                bool getLazyFeedResponse,
                Guid correlatedActivityId)
            {
                if (client == null)
                {
                    throw new ArgumentNullException($"{nameof(client)} can not be null.");
                }

                if (resourceType == null)
                {
                    throw new ArgumentNullException($"{nameof(resourceType)} can not be null.");
                }

                if (expression == null)
                {
                    throw new ArgumentNullException($"{nameof(expression)} can not be null.");
                }

                if (queryRequestOptions == null)
                {
                    throw new ArgumentNullException($"{nameof(queryRequestOptions)} can not be null.");
                }

                if (correlatedActivityId == Guid.Empty)
                {
                    throw new ArgumentException($"{nameof(correlatedActivityId)} can not be empty.");
                }

                this.Client = client;
                this.ResourceTypeEnum = resourceTypeEnum;
                this.ResourceType = resourceType;
                this.Expression = expression;
                this.RequestOptions = queryRequestOptions;
                this.ResourceLink = resourceLink;
                this.GetLazyFeedResponse = getLazyFeedResponse;
                this.CorrelatedActivityId = correlatedActivityId;
            }
        }

        public static readonly FeedResponse<dynamic> EmptyFeedResponse = new FeedResponse<dynamic>(
            Enumerable.Empty<dynamic>(),
            Enumerable.Empty<dynamic>().Count(),
            new StringKeyValueCollection());
        protected SqlQuerySpec querySpec;
        private readonly IDocumentQueryClient client;
        private readonly ResourceType resourceTypeEnum;
        private readonly Type resourceType;
        private readonly Expression expression;
        private readonly CosmosQueryRequestOptions queryRequestOptions;
        private readonly string resourceLink;
        private readonly bool getLazyFeedResponse;
        private bool isExpressionEvaluated;
        private FeedResponse<CosmosElement> lastPage;
        private readonly Guid correlatedActivityId;

        protected DocumentQueryExecutionContextBase(
           InitParams initParams)
        {
            this.client = initParams.Client;
            this.resourceTypeEnum = initParams.ResourceTypeEnum;
            this.resourceType = initParams.ResourceType;
            this.expression = initParams.Expression;
            this.queryRequestOptions = initParams.RequestOptions;
            this.resourceLink = initParams.ResourceLink;
            this.getLazyFeedResponse = initParams.GetLazyFeedResponse;
            this.correlatedActivityId = initParams.CorrelatedActivityId;
            this.isExpressionEvaluated = false;
        }

        public bool ShouldExecuteQueryRequest => this.QuerySpec != null;

        public IDocumentQueryClient Client => this.client;

        public Type ResourceType => this.resourceType;

        public ResourceType ResourceTypeEnum => this.resourceTypeEnum;

        public string ResourceLink => this.resourceLink;

        public int? MaxItemCount => this.queryRequestOptions.MaxItemCount;

        protected SqlQuerySpec QuerySpec
        {
            get
            {
                if (!this.isExpressionEvaluated)
                {
                    this.querySpec = DocumentQueryEvaluator.Evaluate(this.expression);
                    this.isExpressionEvaluated = true;
                }

                return this.querySpec;
            }
        }

        protected PartitionKeyInternal PartitionKeyInternal => this.queryRequestOptions.PartitionKey == null ? null : this.queryRequestOptions.PartitionKey.InternalKey;

        protected int MaxBufferedItemCount => this.queryRequestOptions.MaxBufferedItemCount;

        protected int MaxDegreeOfParallelism => this.queryRequestOptions.MaxConcurrency;

        protected string PartitionKeyRangeId => this.queryRequestOptions.PartitionKeyRangeId;

        protected virtual string ContinuationToken => this.lastPage == null ? this.queryRequestOptions.RequestContinuation : this.lastPage.ResponseContinuation;

        public virtual bool IsDone => this.lastPage != null && string.IsNullOrEmpty(this.lastPage.ResponseContinuation);

        public Guid CorrelatedActivityId => this.correlatedActivityId;

        public async Task<PartitionedQueryExecutionInfo> GetPartitionedQueryExecutionInfoAsync(
            PartitionKeyDefinition partitionKeyDefinition,
            bool requireFormattableOrderByQuery,
            bool isContinuationExpected,
            CancellationToken cancellationToken)
        {
            // $ISSUE-felixfan-2016-07-13: We should probably get PartitionedQueryExecutionInfo from Gateway in GatewayMode

            QueryPartitionProvider queryPartitionProvider = await this.client.GetQueryPartitionProviderAsync(cancellationToken);
            return queryPartitionProvider.GetPartitionedQueryExecutionInfo(this.QuerySpec, partitionKeyDefinition, requireFormattableOrderByQuery, isContinuationExpected);
        }

        public virtual async Task<FeedResponse<CosmosElement>> ExecuteNextAsync(CancellationToken cancellationToken)
        {
            if (this.IsDone)
            {
                throw new InvalidOperationException(RMResources.DocumentQueryExecutionContextIsDone);
            }

            this.lastPage = await ExecuteInternalAsync(cancellationToken);
            return this.lastPage;
        }

        public CosmosQueryRequestOptions GetRequestOptions(string continuationToken)
        {
            CosmosQueryRequestOptions options = this.queryRequestOptions.Clone();
            options.RequestContinuation = continuationToken;
            return options;
        }

        public async Task<INameValueCollection> CreateCommonHeadersAsync(CosmosQueryRequestOptions queryRequestOptions)
        {
            if (queryRequestOptions.ConsistencyLevel.HasValue)
            {
                await this.client.EnsureValidOverwrite(queryRequestOptions.ConsistencyLevel.Value);
            }
            
            ConsistencyLevel defaultConsistencyLevel = await this.client.GetDefaultConsistencyLevelAsync();
            ConsistencyLevel? desiredConsistencyLevel = await this.client.GetDesiredConsistencyLevelAsync();

            return this.queryRequestOptions.CreateCommonHeadersAsync(
                queryRequestOptions,
                defaultConsistencyLevel,
                desiredConsistencyLevel,
                this.resourceTypeEnum);
        }

        public DocumentServiceRequest CreateDocumentServiceRequest(INameValueCollection requestHeaders, SqlQuerySpec querySpec, PartitionKeyInternal partitionKey)
        {
            DocumentServiceRequest request = CreateDocumentServiceRequest(requestHeaders, querySpec);
            PopulatePartitionKeyInfo(request, partitionKey);
            request.Properties = this.queryRequestOptions.Properties;
            return request;
        }

        public DocumentServiceRequest CreateDocumentServiceRequest(INameValueCollection requestHeaders, SqlQuerySpec querySpec, PartitionKeyRange targetRange, string collectionRid)
        {
            DocumentServiceRequest request = CreateDocumentServiceRequest(requestHeaders, querySpec);

            PopulatePartitionKeyRangeInfo(request, targetRange, collectionRid);
            request.Properties = this.queryRequestOptions.Properties;
            return request;
        }

        public async Task<FeedResponse<CosmosElement>> ExecuteRequestLazyAsync(
            DocumentServiceRequest request,
            CancellationToken cancellationToken)
        {
            DocumentServiceResponse documentServiceResponse = await ExecuteQueryRequestInternalAsync(
                request,
                cancellationToken);

            return this.GetFeedResponse(request, documentServiceResponse);
        }

        public async Task<FeedResponse<CosmosElement>> ExecuteRequestAsync(
           DocumentServiceRequest request,
           CancellationToken cancellationToken)
        {
            return await (this.ShouldExecuteQueryRequest ?
                this.ExecuteQueryRequestAsync(request, cancellationToken) :
                this.ExecuteReadFeedRequestAsync(request, cancellationToken));
        }

        public async Task<FeedResponse<T>> ExecuteRequestAsync<T>(
            DocumentServiceRequest request,
            CancellationToken cancellationToken)
        {
            return await (this.ShouldExecuteQueryRequest ?
                ExecuteQueryRequestAsync<T>(request, cancellationToken) :
                ExecuteReadFeedRequestAsync<T>(request, cancellationToken));
        }

        public async Task<FeedResponse<CosmosElement>> ExecuteQueryRequestAsync(
            DocumentServiceRequest request,
            CancellationToken cancellationToken)
        {
            return this.GetFeedResponse(request, await this.ExecuteQueryRequestInternalAsync(request, cancellationToken));
        }

        public async Task<FeedResponse<T>> ExecuteQueryRequestAsync<T>(
            DocumentServiceRequest request,
            CancellationToken cancellationToken)
        {
            return GetFeedResponse<T>(await ExecuteQueryRequestInternalAsync(request, cancellationToken));
        }

        public async Task<FeedResponse<CosmosElement>> ExecuteReadFeedRequestAsync(
            DocumentServiceRequest request,
            CancellationToken cancellationToken)
        {
            return GetFeedResponse(request, await this.client.ReadFeedAsync(request, cancellationToken));
        }

        public async Task<FeedResponse<T>> ExecuteReadFeedRequestAsync<T>(
            DocumentServiceRequest request,
            CancellationToken cancellationToken)
        {
            return GetFeedResponse<T>(await this.client.ReadFeedAsync(request, cancellationToken));
        }

        public void PopulatePartitionKeyRangeInfo(DocumentServiceRequest request, PartitionKeyRange range, string collectionRid)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            if (range == null)
            {
                throw new ArgumentNullException("range");
            }

            if (this.resourceTypeEnum.IsPartitioned())
            {
                request.RouteTo(new PartitionKeyRangeIdentity(collectionRid, range.Id));
            }
        }

        public async Task<PartitionKeyRange> GetTargetPartitionKeyRangeById(string collectionResourceId, string partitionKeyRangeId)
        {
            IRoutingMapProvider routingMapProvider = await this.client.GetRoutingMapProviderAsync();

            PartitionKeyRange range = await routingMapProvider.TryGetPartitionKeyRangeByIdAsync(collectionResourceId, partitionKeyRangeId);
            if (range == null && PathsHelper.IsNameBased(this.resourceLink))
            {
                // Refresh the cache and don't try to reresolve collection as it is not clear what already
                // happened based on previously resolved collection rid.
                // Return NotFoundException this time. Next query will succeed.
                // This can only happen if collection is deleted/created with same name and client was not restarted
                // inbetween.
                CollectionCache collectionCache = await this.Client.GetCollectionCacheAsync();
                collectionCache.Refresh(this.resourceLink);
            }

            if (range == null)
            {
                throw new NotFoundException($"{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}: GetTargetPartitionKeyRangeById(collectionResourceId:{collectionResourceId}, partitionKeyRangeId: {partitionKeyRangeId}) failed due to stale cache");
            }

            return range;
        }

        public async Task<List<PartitionKeyRange>> GetTargetPartitionKeyRanges(string collectionResourceId, List<Range<string>> providedRanges)
        {
            IRoutingMapProvider routingMapProvider = await this.client.GetRoutingMapProviderAsync();

            List<PartitionKeyRange> ranges = await routingMapProvider.TryGetOverlappingRangesAsync(collectionResourceId, providedRanges);
            if (ranges == null && PathsHelper.IsNameBased(this.resourceLink))
            {
                // Refresh the cache and don't try to reresolve collection as it is not clear what already
                // happened based on previously resolved collection rid.
                // Return NotFoundException this time. Next query will succeed.
                // This can only happen if collection is deleted/created with same name and client was not restarted
                // inbetween.
                CollectionCache collectionCache = await this.Client.GetCollectionCacheAsync();
                collectionCache.Refresh(this.resourceLink);
            }

            if (ranges == null)
            {
                throw new NotFoundException($"{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}: GetTargetPartitionKeyRanges(collectionResourceId:{collectionResourceId}, providedRanges: {string.Join(",", providedRanges)} failed due to stale cache");
            }

            return ranges;
        }

        public abstract void Dispose();

        protected abstract Task<FeedResponse<CosmosElement>> ExecuteInternalAsync(CancellationToken cancellationToken);

        protected async Task<List<PartitionKeyRange>> GetReplacementRanges(PartitionKeyRange targetRange, string collectionRid)
        {
            IRoutingMapProvider routingMapProvider = await this.client.GetRoutingMapProviderAsync();
            List<PartitionKeyRange> replacementRanges = (await routingMapProvider.TryGetOverlappingRangesAsync(collectionRid, targetRange.ToRange(), true)).ToList();
            string replaceMinInclusive = replacementRanges.First().MinInclusive;
            string replaceMaxExclusive = replacementRanges.Last().MaxExclusive;
            if (!replaceMinInclusive.Equals(targetRange.MinInclusive, StringComparison.Ordinal) || !replaceMaxExclusive.Equals(targetRange.MaxExclusive, StringComparison.Ordinal))
            {
                throw new InternalServerErrorException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Target range and Replacement range has mismatched min/max. Target range: [{0}, {1}). Replacement range: [{2}, {3}).",
                    targetRange.MinInclusive,
                    targetRange.MaxExclusive,
                    replaceMinInclusive,
                    replaceMaxExclusive));
            }

            return replacementRanges;
        }

        protected bool NeedPartitionKeyRangeCacheRefresh(DocumentClientException ex)
        {
            return ex.StatusCode == (HttpStatusCode)StatusCodes.Gone && ex.GetSubStatus() == SubStatusCodes.PartitionKeyRangeGone;
        }

        private async Task<DocumentServiceResponse> ExecuteQueryRequestInternalAsync(
            DocumentServiceRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                return await this.client.ExecuteQueryAsync(request, cancellationToken);
            }
            finally
            {
                request.Body.Position = 0;
            }
        }

        private DocumentServiceRequest CreateDocumentServiceRequest(INameValueCollection requestHeaders, SqlQuerySpec querySpec)
        {
            DocumentServiceRequest request = querySpec != null ?
                CreateQueryDocumentServiceRequest(requestHeaders, querySpec) :
                CreateReadFeedDocumentServiceRequest(requestHeaders);

            if (this.queryRequestOptions != null)
            {
                request.SerializerSettings = this.queryRequestOptions.JsonSerializerSettings;
            }

            return request;
        }

        private DocumentServiceRequest CreateQueryDocumentServiceRequest(INameValueCollection requestHeaders, SqlQuerySpec querySpec)
        {
            DocumentServiceRequest executeQueryRequest;

            string queryText;
            switch (this.client.QueryCompatibilityMode)
            {
                case QueryCompatibilityMode.SqlQuery:
                    if (querySpec.Parameters != null && querySpec.Parameters.Count > 0)
                    {
                        throw new ArgumentException(
                            string.Format(CultureInfo.InvariantCulture, "Unsupported argument in query compatibility mode '{0}'", this.client.QueryCompatibilityMode),
                            "querySpec.Parameters");
                    }

                    executeQueryRequest = DocumentServiceRequest.Create(
                        OperationType.SqlQuery,
                        this.resourceTypeEnum,
                        this.resourceLink,
                        AuthorizationTokenType.PrimaryMasterKey,
                        requestHeaders);

                    executeQueryRequest.Headers[HttpConstants.HttpHeaders.ContentType] = RuntimeConstants.MediaTypes.SQL;
                    queryText = querySpec.QueryText;
                    break;

                case QueryCompatibilityMode.Default:
                case QueryCompatibilityMode.Query:
                default:
                    executeQueryRequest = DocumentServiceRequest.Create(
                        OperationType.Query,
                        this.resourceTypeEnum,
                        this.resourceLink,
                        AuthorizationTokenType.PrimaryMasterKey,
                        requestHeaders);

                    executeQueryRequest.Headers[HttpConstants.HttpHeaders.ContentType] = RuntimeConstants.MediaTypes.QueryJson;
                    queryText = JsonConvert.SerializeObject(querySpec);
                    break;
            }

            executeQueryRequest.Body = new MemoryStream(Encoding.UTF8.GetBytes(queryText));
            return executeQueryRequest;
        }

        private DocumentServiceRequest CreateReadFeedDocumentServiceRequest(INameValueCollection requestHeaders)
        {
            if (this.resourceTypeEnum == Microsoft.Azure.Cosmos.Internal.ResourceType.Database
                || this.resourceTypeEnum == Microsoft.Azure.Cosmos.Internal.ResourceType.Offer)
            {
                return DocumentServiceRequest.Create(
                    OperationType.ReadFeed,
                    null,
                    this.resourceTypeEnum,
                    AuthorizationTokenType.PrimaryMasterKey,
                    requestHeaders);
            }
            else
            {
                return DocumentServiceRequest.Create(
                   OperationType.ReadFeed,
                   this.resourceTypeEnum,
                   this.resourceLink,
                   AuthorizationTokenType.PrimaryMasterKey,
                   requestHeaders);
            }
        }

        private void PopulatePartitionKeyInfo(DocumentServiceRequest request, PartitionKeyInternal partitionKey)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            if (this.resourceTypeEnum.IsPartitioned())
            {
                if (partitionKey != null)
                {
                    request.Headers[HttpConstants.HttpHeaders.PartitionKey] = partitionKey.ToJsonString();
                }
            }
        }

        private FeedResponse<T> GetFeedResponse<T>(DocumentServiceResponse response)
        {

            long responseLengthBytes = response.ResponseBody.CanSeek ? response.ResponseBody.Length : 0;
            IEnumerable<T> responseFeed = response.GetQueryResponse<T>(this.resourceType, this.getLazyFeedResponse, out int itemCount);

            return new FeedResponse<T>(responseFeed, itemCount, response.Headers, response.RequestStats, responseLengthBytes);
        }

        private FeedResponse<CosmosElement> GetFeedResponse(
            DocumentServiceRequest documentServiceRequest, 
            DocumentServiceResponse documentServiceResponse)
        {
            // Execute the callback an each element of the page
            // For example just could get a response like this
            // {
            //    "_rid": "qHVdAImeKAQ=",
            //    "Documents": [{
            //        "id": "03230",
            //        "_rid": "qHVdAImeKAQBAAAAAAAAAA==",
            //        "_self": "dbs\/qHVdAA==\/colls\/qHVdAImeKAQ=\/docs\/qHVdAImeKAQBAAAAAAAAAA==\/",
            //        "_etag": "\"410000b0-0000-0000-0000-597916b00000\"",
            //        "_attachments": "attachments\/",
            //        "_ts": 1501107886
            //    }],
            //    "_count": 1
            // }
            // And you should execute the callback on each document in "Documents".
            MemoryStream memoryStream = new MemoryStream();
            documentServiceResponse.ResponseBody.CopyTo(memoryStream);
            long responseLengthBytes = memoryStream.Length;
            byte[] content = memoryStream.ToArray();
            IJsonNavigator jsonNavigator = null;

            // Use the users custom navigator first. If it returns null back try the
            // internal navigator.
            if (this.queryRequestOptions.CosmosSerializationOptions != null)
            {
                jsonNavigator = this.queryRequestOptions.CosmosSerializationOptions.CreateCustomNavigatorCallback(content);
                if (jsonNavigator == null)
                {
                    throw new InvalidOperationException("The CosmosSerializationOptions did not return a JSON navigator.");
                }
            }
            else
            {
                jsonNavigator = JsonNavigator.Create(content);
            }

            string resourceName = GetRootNodeName(documentServiceRequest.ResourceType);

            if (!jsonNavigator.TryGetObjectProperty(
                jsonNavigator.GetRootNode(),
                resourceName,
                out ObjectProperty objectProperty))
            {
                throw new InvalidOperationException($"Response Body Contract was violated. QueryResponse did not have property: {resourceName}");
            }

            IJsonNavigatorNode cosmosElements = objectProperty.ValueNode;
            if (!(CosmosElement.Dispatch(
                jsonNavigator,
                cosmosElements) is CosmosArray cosmosArray))
            {
                throw new InvalidOperationException($"QueryResponse did not have an array of : {resourceName}");
            }

            int itemCount = cosmosArray.Count;
            return new FeedResponse<CosmosElement>(
                cosmosArray,
                itemCount,
                documentServiceResponse.Headers,
                documentServiceResponse.RequestStats,
                responseLengthBytes);
        }

        private string GetRootNodeName(ResourceType resourceType)
        {
            switch (resourceType)
            {
                case Internal.ResourceType.Collection:
                    return "DocumentCollections";
                default:
                    return resourceType.ToResourceTypeString() + "s";
            }
        }
    }
}