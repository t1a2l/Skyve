using Extensions;

using Skyve.Domain;
using Skyve.Domain.Systems;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Skyve.Systems;

internal class ImageSystem : IImageService
{
	private readonly Dictionary<string, object> _lockObjects = [];
	private readonly System.Timers.Timer _cacheClearTimer;
	private readonly Dictionary<string, (Bitmap image, DateTime lastAccessed)> _cache = [];
	private readonly TimeSpan _expirationTime = TimeSpan.FromMinutes(1);
	private readonly HttpClient _httpClient = new();
    private readonly ImageProcessor _imageProcessor;
	private readonly INotifier _notifier;

	public ImageSystem(INotifier notifier)
	{
		_imageProcessor = new(this);
		_cacheClearTimer = new System.Timers.Timer(_expirationTime.TotalMilliseconds);
		_cacheClearTimer.Elapsed += CacheClearTimer_Elapsed;
		_cacheClearTimer.Start();
		_notifier = notifier;
	}

	private object LockObj(string path)
	{
		lock (_lockObjects)
		{
			if (!_lockObjects.ContainsKey(path))
			{
				_lockObjects.Add(path, new object());
			}

			return _lockObjects[path];
		}
	}

	public FileInfo File(string url, string? fileName = null)
	{
		var filePath = CrossIO.Combine(ISave.DocsFolder, "Thumbs", fileName ?? Path.GetFileNameWithoutExtension(RemoveQueryParamsFromUrl(url).TrimEnd('/', '\\')) + Path.GetExtension(url).IfEmpty(".png"));

		return new FileInfo(filePath);
	}

	private string RemoveQueryParamsFromUrl(string url)
	{
		var index = url.IndexOf('?');
		return index >= 0 ? url.Substring(0, index) : url;
	}

	public async Task<Bitmap?> GetImage(string? url)
	{
		var image = await GetImage(url, false);

		return image is not null ? new(image) : null;
	}

	public async Task<Bitmap?> GetImage(string? url, bool localOnly, string? fileName = null, bool square = true)
	{
		if (url is null || !await Ensure(url, localOnly, fileName, square))
		{
			return null;
		}

		var cache = GetCache(url);

		if (cache != null)
		{
			return cache;
		}

		var filePath = File(url, fileName);

		if (filePath.Exists)
		{
			lock (LockObj(url))
			{
				try
				{
					return AddCache(url, (Bitmap)Image.FromFile(filePath.FullName));
				}
				catch { }
			}
		}

		return null;
	}

	public async Task<bool> Ensure(string? url, bool localOnly = false, string? fileName = null, bool square = true)
	{
		if (url is null or "")
		{
			return false;
		}

		var filePath = File(url, fileName);

		lock (LockObj(url))
		{
			if (filePath.Exists)
			{
				return true;
			}

			if (localOnly)
			{
				_imageProcessor.Add(new(url, fileName, square));

				return false;
			}
		}

		var tries = 1;
		start:

		if (!ConnectionHandler.IsConnected)
		{
			return false;
		}

		try
		{
			// check url is ok
            var request = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await _httpClient.SendAsync(request);

            if(!response.IsSuccessStatusCode && url != null)
			{
                string[] parts = url.Split('/');
                int appsIndex = Array.IndexOf(parts, "apps");
				string appId = parts[appsIndex + 1]; // 271590

				var newUrl = "https://store.steampowered.com/api/appdetails?appids=" + appId + "&filters=basic";

				var request1 = new HttpRequestMessage(HttpMethod.Head, newUrl);

                var apiResponse = await _httpClient.SendAsync(request1);

                if (!apiResponse.IsSuccessStatusCode)
				{
                    return false;
                }

				var str = await _httpClient.GetStringAsync(newUrl);

                JObject obj = JObject.Parse(str);

                url = obj[appId]?["data"]?["header_image"]?.Value<string>();
            }

            using var ms = await _httpClient.GetStreamAsync(url);

            using var img = Image.FromStream(ms);

			var squareSize = Math.Min(img.Width, 512);
			var size = string.IsNullOrEmpty(fileName) ? img.Size.GetProportionalDownscaledSize(squareSize) : img.Size;
			using var image = string.IsNullOrEmpty(fileName) ? square ? new Bitmap(squareSize, squareSize) : new Bitmap(size.Width, size.Height) : img;

			if (string.IsNullOrEmpty(fileName))
			{
				using var imageGraphics = Graphics.FromImage(image);

				imageGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
				imageGraphics.DrawImage(img, square
					? new Rectangle((squareSize - size.Width) / 2, (squareSize - size.Height) / 2, size.Width, size.Height)
					: new Rectangle(Point.Empty, size));
			}

			Directory.GetParent(filePath.FullName).Create();

			if(url != null)
			{
                lock (LockObj(url))
                {
                    if (filePath.FullName.EndsWith(".jpg", StringComparison.InvariantCultureIgnoreCase) || filePath.FullName.EndsWith(".jpeg", StringComparison.InvariantCultureIgnoreCase))
                    {
                        image.Save(filePath.FullName, System.Drawing.Imaging.ImageFormat.Jpeg);
                    }
                    else
                    {
                        image.Save(filePath.FullName);
                    }
                }
            }
			
			_notifier.OnRefreshUI();

			return true;
		}
		catch (Exception ex)
		{
			if (ex is WebException we && we.Response is HttpWebResponse hwr && hwr.StatusCode == HttpStatusCode.BadGateway)
			{
				await Task.Delay(1000);

				goto start;
			}
			else if (tries < 2)
			{
				tries++;
				goto start;
			}

			return false;
		}
	}

	private Bitmap AddCache(string key, Bitmap image)
	{
		if (key is null or "")
		{
			return image;
		}

		if (_cache.ContainsKey(key))
		{
			_cache[key] = (image, DateTime.Now);
		}
		else
		{
			_cache.Add(key, (image, DateTime.Now));
		}

		return image;
	}

	public Bitmap? GetCache(string key)
	{
		if (_cache.TryGetValue(key, out var value))
		{
			if (DateTime.Now - value.lastAccessed > _expirationTime)
			{
				value.image.Dispose();
				_ = _cache.Remove(key);
				return null;
			}

			value.lastAccessed = DateTime.Now;
			return value.image;
		}

		return null;
	}

	private void CacheClearTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
	{
		var sw = Stopwatch.StartNew();

		try
		{
			var keys = _cache.Keys.ToList();

			foreach (var key in keys)
			{
				if (_cache.TryGetValue(key, out var value))
				{
					if (DateTime.Now - value.lastAccessed > _expirationTime)
					{
						value.image.Dispose();
						_cache.Remove(key);
					}
				}
			}
		}
		catch { }

		sw.Stop();

		if (sw.ElapsedMilliseconds > 5000)
		{
			ServiceCenter.Get<ILogger>().Info("[Auto] [Timer] Cleared Image Cache");
		}
	}

	public void ClearCache()
	{
		lock (_lockObjects)
		{
			foreach (var (image, lastAccessed) in _cache.Values)
			{
				image?.Dispose();
			}

			_cache.Clear();

			foreach (var item in Directory.EnumerateFiles(CrossIO.Combine(ISave.DocsFolder, "Thumbs")))
			{
				try
				{
					CrossIO.DeleteFile(item);
				}
				catch { }
			}
		}
	}
}