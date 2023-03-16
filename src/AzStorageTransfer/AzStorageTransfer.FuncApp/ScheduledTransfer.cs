using Amazon.S3;
using Amazon.Util.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AzStorageTransfer.FuncApp
{
    public class ScheduledTransfer
    {
        /// <summary>
        /// Cron expression to schedule execution.
        /// </summary>
        private const string CronSchedule = "0 */60* * * *";

        /// <summary>
        /// Amazon S3 client.
        /// </summary>
        private readonly IAmazonS3 amazonS3;
        private readonly CloudBlobClient cloudBlobClient;
        private readonly CloudBlobContainer scheduledBlobContainer;
        private readonly CloudBlobContainer archiveBlobContainer;
        private readonly CloudBlobContainer liveBlobContainer;

        public ScheduledTransfer(IAmazonS3 amazonS3)
        {
            this.amazonS3 = amazonS3;
            this.cloudBlobClient = CloudStorageAccount.Parse(Config.DataStorageConnection).CreateCloudBlobClient();
            this.scheduledBlobContainer = this.cloudBlobClient.GetContainerReference(Config.ScheduledContainer);
            this.archiveBlobContainer = this.cloudBlobClient.GetContainerReference(Config.ArchiveContainer);
            this.liveBlobContainer = this.cloudBlobClient.GetContainerReference(Config.LiveContainer);
        }

        /// <summary>
        /// Scheduled copy of files from Az blob container 'scheduled' to S3 and then moved to an archive container.
        /// </summary>
        [FunctionName(nameof(ScheduledTransfer))]
        public async Task Run([TimerTrigger(CronSchedule, RunOnStartup = true)] TimerInfo myTimer, ILogger log)
        {
            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            var blobItems = scheduledBlobContainer.ListBlobs(useFlatBlobListing: true);
            foreach (CloudBlockBlob item in blobItems)
            {
                await TrasferAndArchiveBlobAsync(item, log);
            }

            var archiveBlobItems = archiveBlobContainer.ListBlobs(useFlatBlobListing: true);
            foreach(CloudBlockBlob item in archiveBlobItems)
            {
                await CheckArchiveBlobAsync(item, log);
            }

            var liveBlobItems = liveBlobContainer.ListBlobs(useFlatBlobListing: true);
            foreach(CloudBlockBlob item in liveBlobItems)
            {
                await CheckLiveBlobAsync(item, log);
            }
        }

        /// <summary>
        /// Exposes the same functionality described in the ScheduledTranfer but via an HttpTrigger.
        /// Used to integrate with Data Factory if needed.
        /// </summary>
        [FunctionName(nameof(TransferFiles))]
        public async Task<IActionResult> TransferFiles(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "tranferfiles")] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");
            var blobItems = scheduledBlobContainer.ListBlobs(useFlatBlobListing: true);
            foreach (CloudBlockBlob item in blobItems)
            {
                await TrasferAndArchiveBlobAsync(item, log);
            }

            return new OkResult();
        }

        private async Task TrasferAndArchiveBlobAsync(CloudBlockBlob cloudBlob, ILogger log)
        {
            using (var ms = new MemoryStream())
            {
                // Download to stream
                await cloudBlob.DownloadToStreamAsync(ms);
                ms.Seek(0, SeekOrigin.Begin);

                // Transfer to Amazon S3
                //await this.amazonS3.UploadObjectFromStreamAsync(Config.Aws.BucketName, cloudBlob.Name, ms, new Dictionary<string, object>() );
                
                var transfer = new Amazon.S3.Transfer.TransferUtility(amazonS3);
                var request = new Amazon.S3.Transfer.TransferUtilityUploadRequest
                {
                    BucketName = Config.Aws.BucketName,
                    Key = cloudBlob.Name,
                    InputStream = ms,

                };

                request.Headers.ContentMD5 = cloudBlob.Properties.ContentMD5;

                IDictionary<string, object> additionalProperties = new Dictionary<string, object>();
                InternalSDKUtils.ApplyValues(request, additionalProperties);

                transfer.Upload(request);

                log.LogInformation($"File '{cloudBlob.Name}' uploaded to S3.");
            }

            // Copy file to archive container
            var archiveBlob = this.archiveBlobContainer.GetBlockBlobReference(cloudBlob.Name);
            var copyResult = await archiveBlob.StartCopyAsync(cloudBlob);
            log.LogInformation($"File '{cloudBlob.Name}' copied to container: {Config.ArchiveContainer}.");

            // Delete file from scheduled container
            await cloudBlob.DeleteAsync();
            log.LogInformation($"File '{cloudBlob.Name}' deleted from container: {Config.ScheduledContainer}.");
        }

        private async Task CheckArchiveBlobAsync(CloudBlockBlob cloudBlob, ILogger log)
        {
            if(cloudBlob.Properties.LastModified != null)
            {
                var dateTime = cloudBlob.Properties.LastModified.Value.UtcDateTime;
                TimeSpan ts = DateTime.UtcNow - dateTime;
                if(ts.TotalDays >= 14)
                {
                    // Delete file from scheduled container
                    await cloudBlob.DeleteAsync();
                    log.LogInformation($"File '{cloudBlob.Name}' deleted from container: {Config.ScheduledContainer}.");
                }
            }

        }

        private async Task CheckLiveBlobAsync(CloudBlockBlob cloudBlob, ILogger log)
        {
            if(cloudBlob.Properties.LastModified != null)
            {
                var dateTime = cloudBlob.Properties.LastModified.Value.UtcDateTime;
                TimeSpan ts = DateTime.UtcNow - dateTime;
                if(ts.TotalDays >= 14)
                {
                    // Delete file from scheduled container
                    await cloudBlob.DeleteAsync();
                    log.LogInformation($"File '{cloudBlob.Name}' deleted from container: {Config.ScheduledContainer}.");
                }
            }

        }
    }
}
