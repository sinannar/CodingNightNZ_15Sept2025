﻿using System.Net;
using Microsoft.AspNetCore.Components.Forms;
using Azure.Storage.Blobs;
using ImageGallery.Shared;

namespace ImageGallery.FrontEnd.Components.Pages;

public sealed partial class Home(
    [FromKeyedServices("images")] BlobContainerClient imagesContainerClient,
    [FromKeyedServices("thumbnails")] BlobContainerClient thumbsContainerClient,
    QueueMessageHandler queueMessageHandler,
    ILogger<Home> logger)
{
    private const long UploadFileSizeLimitBytes = 524_288; // 512 KB (512 x 1024 bytes)

    private readonly HashSet<ImageViewModel> _images = [];

    private bool _isDialogOpen;
    private string _dialogMessage = "";
    private Guid _fileUploadKey = Guid.CreateVersion7();

    private Task OnMessageReceivedAsync(UploadResult uploadResult) => InvokeAsync(LoadBlobsAsync);

    private void OpenDialog() => _isDialogOpen = true;

    private void CloseDialog() => _isDialogOpen = false;

    protected override async Task OnInitializedAsync()
    {
        logger.LogDebug("Subscribing to message handler.");

        await LoadBlobsAsync();

        queueMessageHandler.MessageReceived += OnMessageReceivedAsync;
    }

    private async Task LoadBlobsAsync()
    {
        try
        {
            logger.LogDebug("Loading blobs...");

            await foreach (var blobItem in thumbsContainerClient.GetBlobsAsync())
            {
                _images.Add(new(ImageUrl.GetImageUrl(blobItem.Name), ImageUrl.GetThumbnailUrl(blobItem.Name)));
            }
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task OnFilesChangedAsync(InputFileChangeEventArgs e)
    {
        try
        {
            foreach (var file in e.GetMultipleFiles())
            {
                if (!file.ContentType.StartsWith("image/"))
                {
                    _dialogMessage = $"File {file.Name} is not a valid image.";
                    OpenDialog();

                    continue;
                }

                if (file is { Size: > UploadFileSizeLimitBytes })
                {
                    _dialogMessage = $"File {file.Name} exceeds the size limit of {UploadFileSizeLimitBytes} bytes.";
                    OpenDialog();

                    continue;
                }

                logger.LogDebug("Uploading {Name}", file.Name);

                var slug = ImageUrl.CreateNameSlug(file.Name);
                var blobClient = imagesContainerClient.GetBlobClient(slug);

                using var stream = file.OpenReadStream();

                await blobClient.UploadAsync(stream, overwrite: true);

                var metadata = new Dictionary<string, string>
                {
                    ["OriginalFileName"] = WebUtility.UrlEncode(file.Name)
                };
                await blobClient.SetMetadataAsync(metadata);

                logger.LogInformation("Uploaded {Name} with slug {Slug}", file.Name, slug);
            }
        }
        finally
        {
            _fileUploadKey = Guid.CreateVersion7();

            await InvokeAsync(StateHasChanged);
        }
    }

    internal sealed record class ImageViewModel(
        string? ImageUrl,
        string? ThumbnailUrl);
}
