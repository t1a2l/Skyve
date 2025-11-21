using Extensions;

using Skyve.Domain.Systems;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Skyve.Systems;
internal class ImageProcessor : PeriodicProcessor<ImageProcessor.ImageRequest, ImageProcessor.TimeStampedImage>
{
	private readonly IImageService _imageManager;

	public ImageProcessor(IImageService imageManager) : base(100, 500, null) 
	{
		_imageManager = imageManager;
	}

    protected override bool CanProcess()
	{
		return ConnectionHandler.IsConnected;
	}


	protected override async Task<(Dictionary<ImageRequest, TimeStampedImage>, bool)> ProcessItems(List<ImageRequest> entities)
	{
		foreach (var img in entities)
		{
			if (!string.IsNullOrWhiteSpace(img.Url))
			{
				await _imageManager.Ensure(img.Url, false, img.FileName, img.Square);
			}
		}

		return ([], false);
	}

	protected override void CacheItems(Dictionary<ImageRequest, TimeStampedImage> results)
	{ }

	internal readonly struct ImageRequest(string url, string? fileName, bool square)
    {
        public string Url { get; } = url;
        public string? FileName { get; } = fileName;
        public bool Square { get; } = square;
    }

	internal readonly struct TimeStampedImage : ITimestamped
	{
		public DateTime Timestamp { get; }
	}
}
