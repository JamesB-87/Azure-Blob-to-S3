using Amazon.S3;
using Amazon.Util.Internal;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;

namespace AzStorageTransfer.FuncApp
{
    public class FileTriggeredTransfer
    {
        private readonly IAmazonS3 amazonS3;

        public FileTriggeredTransfer(IAmazonS3 amazonS3)
        {
            this.amazonS3 = amazonS3;
        }

        [FunctionName(nameof(FileTriggeredTransfer))]
        public async Task Run(
            [BlobTrigger("%ArchiveContainer%/{name}", Connection = nameof(Config.DataStorageConnection))]
            ICloudBlob myBlob,
            string name,
            ILogger log)
        {
            log.LogInformation($"C# Blob trigger function Processed blob\n Name:{name} \n Size: {myBlob.Properties.Length} Bytes");



            using (var ms = new MemoryStream())
            {
                // Download blob content to stream
                await myBlob.DownloadToStreamAsync(ms);
                ms.Seek(0, SeekOrigin.Begin);

                // Upload stream to S3
                //await this.amazonS3.UploadObjectFromStreamAsync(Config.Aws.BucketName, name, ms, new Dictionary<string, object>());

                var transfer = new Amazon.S3.Transfer.TransferUtility(amazonS3);
                var request = new Amazon.S3.Transfer.TransferUtilityUploadRequest
                {
                    BucketName = Config.Aws.BucketName,
                    Key = name,
                    InputStream = ms,
                    
                };
                
                request.Headers.ContentMD5 = myBlob.Properties.ContentMD5;

                IDictionary<string, object> additionalProperties = new Dictionary<string, object>();
                InternalSDKUtils.ApplyValues(request, additionalProperties);
                
                transfer.Upload(request);
            }
        }

    }
    
}
