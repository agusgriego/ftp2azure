﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.StorageClient;
using AzureFtpServer.Provider;

namespace AzureFtpServer.Provider {

    public class StorageProviderEventArgs : EventArgs {
        public StorageOperation Operation;
        public StorageOperationResult Result;
    }

    public sealed class AzureBlobStorageProvider
    {
        // Events

        #region Delegates

        public delegate void PutCompletedEventHandler(AzureCloudFile o, StorageOperationResult r);

        #endregion

        private CloudStorageAccount _account;
        private CloudBlobClient _blobClient;
        private CloudBlob blob;

        public AzureBlobStorageProvider(String containerName)
        {
            Initialise(containerName);
        }

        // Default constructor, required for reflection.
        public AzureBlobStorageProvider()
        {
            Initialise(null); // HTTPS disabled; Asynch Calls enabled by default.
        }

        private Uri BaseUri
        {
            get { return new Uri(StorageProviderConfiguration.BaseUri + "/" + StorageProviderConfiguration.AccountName + @"/" + ContainerName); }
        }

        public bool UseHttps { get; private set; }

        public Boolean RetryOnTimeout { get; set; }
        public Boolean UseAsynchCalls { get; set; }
        public String ContainerName { private get; set; }

        #region IStorageProvider Members

        /// <summary>
        /// Occurs when a storage provider operation has completed.
        /// </summary>
        public event EventHandler<StorageProviderEventArgs> StorageProviderOperationCompleted;

        public String FolderDelimiter
        {
            get { return "/"; }
        }

        #endregion

        // Delegates

        // Initialiser method
        private void Initialise(String containerName)
        {
           
            if (String.IsNullOrEmpty(containerName))
                throw new ArgumentException("You must provide the base Container Name", "containerName");
            
            ContainerName = containerName;

            if (StorageProviderConfiguration.Mode == Modes.Development || StorageProviderConfiguration.Mode == Modes.Debug)
            {
                _account = CloudStorageAccount.DevelopmentStorageAccount;
                _blobClient = _account.CreateCloudBlobClient();
                _blobClient.Timeout = new TimeSpan(0, 0, 0, 5);
            }
            else
            {
                _account = new CloudStorageAccount(
                    new StorageCredentialsAccountAndKey(StorageProviderConfiguration.AccountName, StorageProviderConfiguration.AccountKey), UseHttps);
                _blobClient = _account.CreateCloudBlobClient();
                _blobClient.Timeout = new TimeSpan(0, 0, 0, 5);
            }

            _blobClient.GetContainerReference(ContainerName).CreateIfNotExist();
            
        }

        #region "Storage operations"

        /// <summary>
        /// Puts the specified object onto the cloud storage provider.
        /// </summary>
        /// <param name="o">The object to store.</param>
        public void Put(AzureCloudFile o)
        {
            if (o.Data == null)
                throw new ArgumentNullException("o", "AzureCloudFile cannot be null.");

            if (o.Uri == null)
                throw new ArgumentException("Parameter 'Uri' of argument 'o' cannot be null.");

            string path = o.Uri.ToString();

            if (path.StartsWith(@"/"))
                path = path.Remove(0, 1);

            if (path.StartsWith(@"\\"))
                path = path.Remove(0, 1);

            if (path.StartsWith(@"\"))
                path = path.Remove(0, 1);

            // Remove double back slashes from anywhere in the path
            path = path.Replace(@"\\", @"\");

            CloudBlobContainer container = _blobClient.GetContainerReference(ContainerName);
            container.CreateIfNotExist();

            // Set permissions on the container
            BlobContainerPermissions perms = container.GetPermissions();
            if (perms.PublicAccess != BlobContainerPublicAccessType.Container)
            {
                perms.PublicAccess = BlobContainerPublicAccessType.Container;
                container.SetPermissions(perms);
            }


            // Create a reference for the filename
            String uniqueName = path;
            blob = container.GetBlobReference(uniqueName);

            // Create a new AsyncCallback instance
            AsyncCallback callback = PutOperationCompleteCallback;


            
            blob.BeginUploadFromStream(new MemoryStream(o.Data), callback, o.Uri);
            

            // Uncomment for synchronous upload
            // blob.UploadFromStream(new System.IO.MemoryStream(o.Data));
        }

        /// <summary>
        /// Delete the specified AzureCloudFile from the Azure container.
        /// </summary>
        /// <param name="o">The object to be deleted.</param>
        public void Delete(AzureCloudFile o)
        {
            string path = UriPathToString(o.Uri);
            if (path.StartsWith("/"))
                path = path.Remove(0, 1);

            CloudBlobContainer c = GetContainerReference(ContainerName);
            CloudBlob b = c.GetBlobReference(path);
            if (b != null)
                b.BeginDelete(new AsyncCallback(DeleteOperationCompleteCallback), o.Uri);
            else
                throw new ArgumentException("The container reference could not be retrieved from storage provider.", "o");
        }

        /// <summary>
        /// Retrieves the object from the storage provider
        /// </summary>
        /// <param name="path">The fully qualified OR relative URI to the object to be retrieved</param>
        /// <param name="downloadData">Boolean indicating whether to download the contents of the file to the Data property or not</param>
        /// <returns>AzureCloudFile</returns>
        /// <exception cref="FileNotFoundException">Throws a FileNotFoundException if the URI is not found on the provider.</exception>
        public AzureCloudFile Get(string path, bool downloadData)
        {
            var u = new Uri(path, UriKind.RelativeOrAbsolute);
            string blobPath = UriPathToString(u);

            if (blobPath.StartsWith(@"/"))
                blobPath = blobPath.Remove(0, 1);

            blobPath = RemoveContainerName(blobPath);

            var o = new AzureCloudFile();
            CloudBlobContainer c = GetContainerReference(ContainerName);

            CloudBlob b = null;

            try
            {
                b = c.GetBlobReference(blobPath);
                b.FetchAttributes();
                o = new AzureCloudFile
                        {
                            Meta = b.Metadata,
                            StorageOperationResult = StorageOperationResult.Completed,
                            Uri = new Uri(blobPath, UriKind.RelativeOrAbsolute),
                            LastModified = b.Properties.LastModifiedUtc,
                            ContentType = b.Properties.ContentType,
                            Size = b.Properties.Length
                        };

                o.Meta.Add("ContentType", b.Properties.ContentType);
            }
            catch (StorageClientException ex)
            {
                if (ex.ErrorCode == StorageErrorCode.BlobNotFound)
                {
                    throw new FileNotFoundException(
                        "The storage provider was unable to locate the object identified by the given URI.",
                        u.ToString());
                }

                if (ex.ErrorCode == StorageErrorCode.ResourceNotFound)
                {
                    return null;
                }
            }

            // Try to download the data for the blob, if requested
            // TODO: Implement asynchronous calls for this
            try
            {
                if (downloadData && b != null)
                {
                    byte[] data = b.DownloadByteArray();
                    o.Data = data;
                }
            }

            catch (TimeoutException)
            {
                if (RetryOnTimeout)
                {
                    Get(blobPath, downloadData); // NOTE: Infinite retries, what fun! :)
                    // TODO: Implement retry attempt limitation
                }
                else
                {
                    throw;
                }
            }

            return o;
        }


        /// <summary>
        /// Checks if a blob exists within the container with the specified path (name).
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns></returns>
        public bool CheckBlobExists(string path)
        {
            string p = path;

            // Remove any leading slashes
            if (p.StartsWith("/"))
            {
                p = p.Remove(0, 1);
            }

            if (p.StartsWith(ContainerName + @"/"))
            {
                p = p.Replace(ContainerName + @"/", @"");
            }

            CloudBlobContainer c = GetContainerReference(ContainerName);
            CloudBlob b = c.GetBlobReference(p);

            try
            {
                b.FetchAttributes();
                return true;
            }
            catch (StorageClientException e)
            {
                if (e.ErrorCode == StorageErrorCode.ResourceNotFound)
                {
                    return false;
                }
                else
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Gets the directory listing of all blobs within the parent container specified.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns></returns>
        public CloudDirectoryCollection GetDirectoryListing(string path)
        {
            path = ParsePath(path);
            CloudBlobContainer container = _blobClient.GetContainerReference(ContainerName);
            var directories = new CloudDirectoryCollection();

            if (path == "")
            {
                directories.AddRange(
                    container.ListBlobs().OfType<CloudBlobDirectory>().Select(
                        dir => new CloudDirectory {Path = dir.Uri.ToString()}));
            }
            else
            {
                CloudBlobDirectory parent = container.GetDirectoryReference(path);
                directories.AddRange(
                    parent.ListBlobs().OfType<CloudBlobDirectory>().Select(
                        dir => new CloudDirectory {Path = dir.Uri.ToString()}));
            }

            return directories;
        }

        public CloudFileCollection GetFileListing(string path)
        {
            String prefix = String.Concat(ContainerName, "/", ParsePath(path));
            var files = new CloudFileCollection();
            files.AddRange(
                _blobClient.ListBlobsWithPrefix(prefix).OfType<CloudBlob>().Select(
                    blob =>
                    new AzureCloudFile
                        {
                            Meta = blob.Metadata,
                            Uri = blob.Uri,
                            Size = blob.Properties.Length,
                            ContentType = blob.Properties.ContentType
                        }));

            return files;
        }

        /// <summary>
        /// Overwrites the object stored at the original path with the new object. Checks if the existing path exists, then calls PUT on the new path.
        /// </summary>
        /// <param name="originalPath">The original path.</param>
        /// <param name="newObject">The new object.</param>
        public void Overwrite(string originalPath, AzureCloudFile newObject)
        {
            // Check if the original path exists on the provider.
            if (!CheckBlobExists(originalPath))
            {
                throw new FileNotFoundException("The path supplied does not exist on the storage provider.",
                                                originalPath);
            }

            // Put the new object over the top of the old...
            Put(newObject);
        }

        /// <summary>
        /// Renames the specified object by copying the original to a new path and deleting the original.
        /// </summary>
        /// <param name="originalPath">The original path.</param>
        /// <param name="newPath">The new path.</param>
        /// <returns></returns>
        public StorageOperationResult Rename(string originalPath, string newPath)
        {
            var u = new Uri(newPath, UriKind.RelativeOrAbsolute);
            CloudBlobContainer c = GetContainerReference(ContainerName);

            newPath = UriPathToString(u);
            if (newPath.StartsWith("/"))
                newPath = newPath.Remove(0, 1);

            originalPath = UriPathToString(new Uri(originalPath, UriKind.RelativeOrAbsolute));
            if (originalPath.StartsWith("/"))
                originalPath = originalPath.Remove(0, 1);

            CloudBlob newBlob = c.GetBlobReference(newPath);
            CloudBlob originalBlob = c.GetBlobReference(originalPath);

            // Check if the original path exists on the provider.
            if (!CheckBlobExists(originalPath))
            {
                throw new FileNotFoundException("The path supplied does not exist on the storage provider.",
                                                originalPath);
            }

            newBlob.CopyFromBlob(originalBlob);

            try
            {
                newBlob.FetchAttributes();
                originalBlob.Delete();
                return StorageOperationResult.Completed;
            }
            catch (StorageClientException e)
            {
                throw;
            }
        }

        public void CreateDirectory(string path)
        {
            if (path.StartsWith("/"))
                path = path.Remove(0, 1);

            CloudBlobContainer container = _blobClient.GetContainerReference(ContainerName);
            string blobName = String.Concat(path, "/required.req");
            CloudBlob blob = container.GetBlobReference(blobName);

            string message = "#REQUIRED: At least one file is required to be present in this folder.";
            byte[] msg = Encoding.ASCII.GetBytes(message);
            blob.UploadByteArray(msg);

            BlobProperties props = blob.Properties;
            props.ContentType = "text/text";
            blob.SetProperties();
        }

        /// <summary>
        /// Determines whether the specified path is a valid blob or folder name (if it exists).
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public bool IsValidPath(string path)
        {
            if (path != null)
                if (path == "/")
                    return true;

            CloudBlobContainer c = GetContainerReference(ContainerName);
            if (c.HasDirectories(path))
                return true;

            if (c.HasFiles(path))
                return true;

            CloudBlob b = c.GetBlobReference(path);
            try
            {
                b.FetchAttributes();
            }
            catch (StorageClientException ex)
            {
                if (ex.ErrorCode == StorageErrorCode.ResourceNotFound)
                    return false;
                else
                {
                    throw;
                }
            }

            return false;
        }

        #endregion

        #region "Helper methods"

        /// <summary>
        /// Returns the container name from the fileNameAndPath parameter.
        /// </summary>
        /// <param name="path">The full URI to the stored object, including the filename.</param>
        /// <returns></returns>
        private static string ExtractContainerName(String path)
        {
            return path.Split('/')[0].ToLower(); // Azure requires URI's in lowercase
        }

        /// <summary>
        /// A helper method to return an initialised CloudBlobContainer object.
        /// </summary>
        /// <param name="containerName">The container to retrieve.</param>
        /// <returns></returns>
        private CloudBlobContainer GetContainerReference(string containerName)
        {
            // Put a reference to the container if one does not exist already
            CloudBlobContainer container = _blobClient.GetContainerReference(containerName);
            container.CreateIfNotExist();

            BlobContainerPermissions permissions = container.GetPermissions();
            if (permissions.PublicAccess != BlobContainerPublicAccessType.Container)
            {
                permissions.PublicAccess = BlobContainerPublicAccessType.Container;
                container.SetPermissions(permissions);
            }

            return container;
        }

        /// <summary>
        /// Parses the path to ensure it is compatible with Azure.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns></returns>
        private String ParsePath(String path)
        {
            if (!path.EndsWith("/"))
                path += "/";

            switch (path)
            {
                case "/":
                    path = "";
                    break;
                default:
                    if (!path.EndsWith("/"))
                    {
                        path += "/";
                    }
                    else
                    {
                        path = path.Remove(0, 1);
                    }

                    break;
            }

            path = path.Replace(@"//", "/");

            return path;
        }

        /// <summary>
        /// Performs .ToString() on the specified URI dependant on type
        /// </summary>
        /// <param name="u"></param>
        /// <returns></returns>
        private string UriPathToString(Uri u)
        {
            if (u.IsAbsoluteUri)
            {
                return u.PathAndQuery;
            }
            else
            {
                return u.ToString();
            }
        }

        /// <summary>
        /// Removes the container name and trailing slash from the specified path.
        /// </summary>
        /// <param name="path">The path you want to strip</param>
        /// <returns></returns>
        private string RemoveContainerName(string path)
        {
            path = path.Replace(ContainerName + @"/", "");
            return path;
        }

        #endregion

        #region "Callbacks"




        /// <summary>
        /// Announce completion of PUT operation
        /// </summary>
        /// <param name="result"></param>
        private void PutOperationCompleteCallback(IAsyncResult result)
        {

            if (blob != null)
            {

                bool propFlag = false;
                switch (Path.GetExtension(blob.Uri.AbsoluteUri))
                {
                    case ".js":
                    case ".html":
                        blob.Properties.ContentType = "text/html";
                        propFlag = true;
                        break;
                    case ".css":
                        blob.Properties.ContentType = "text/css";
                        propFlag = true;
                        break;
                    case ".txt":
                        blob.Properties.ContentType = "text/text";
                        propFlag = true;
                        break;
                    case ".png":
                        blob.Properties.ContentType = "image/png";
                        propFlag = true;
                        break;
                    //
                }
                if (propFlag) blob.SetProperties();

            }

            var o = (Uri) result.AsyncState;
            
            if (StorageProviderOperationCompleted == null) return;
            var a = new StorageProviderEventArgs
                        {Operation = StorageOperation.Put, Result = StorageOperationResult.Created};

            // Raise the event

            //blob.Properties.ContentType = "text/html";
            //blob.BeginSetProperties(ChangeOptionsOperationCompleteCallback, null);




            StorageProviderOperationCompleted(o, a);
        }

        /// <summary>
        /// Announce completion of a Delete operation.
        /// </summary>
        /// <param name="result"></param>
        private void DeleteOperationCompleteCallback(IAsyncResult result)
        {
            var o = (Uri) result.AsyncState;

            if (StorageProviderOperationCompleted == null) return;
            var a = new StorageProviderEventArgs
                        {Operation = StorageOperation.Delete, Result = StorageOperationResult.Deleted};
            // Raise the event
            StorageProviderOperationCompleted(o, a);
        }

        #endregion
    }
}