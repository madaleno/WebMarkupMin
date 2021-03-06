﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

using WebMarkupMin.AspNet.Common;
using WebMarkupMin.AspNet.Common.Compressors;
using WebMarkupMin.Core;
using WebMarkupMin.Core.Utilities;
using AspNetCommonStrings = WebMarkupMin.AspNet.Common.Resources.Strings;

#if ASPNETCORE1
namespace WebMarkupMin.AspNetCore1
#elif ASPNETCORE2
namespace WebMarkupMin.AspNetCore2
#else
#error No implementation for this target
#endif
{
	/// <summary>
	/// Stream wrapper that apply a markup minification and compression only if necessary
	/// </summary>
	internal sealed class BodyWrapperStream : Stream, IHttpBufferingFeature
	{
		/// <summary>
		/// HTTP context
		/// </summary>
		private readonly HttpContext _context;

		/// <summary>
		/// Original stream
		/// </summary>
		private readonly Stream _originalStream;

		/// <summary>
		/// Stream that original content is read into
		/// </summary>
		private MemoryStream _cachedStream;

		/// <summary>
		/// Compression stream
		/// </summary>
		private Stream _compressionStream;

		/// <summary>
		/// Flag for whether to do automatically flush the compression stream
		/// </summary>
		private bool _autoFlushCompressionStream = false;

		/// <summary>
		/// WebMarkupMin configuration
		/// </summary>
		private readonly WebMarkupMinOptions _options;

		/// <summary>
		/// List of markup minification managers
		/// </summary>
		private readonly IList<IMarkupMinificationManager> _minificationManagers;

		/// <summary>
		/// HTTP compression manager
		/// </summary>
		private readonly IHttpCompressionManager _compressionManager;

		/// <summary>
		/// HTTP buffering feature
		/// </summary>
		private readonly IHttpBufferingFeature _bufferingFeature;

		/// <summary>
		/// Flag indicating whether the stream wrapper is initialized
		/// </summary>
		private InterlockedStatedFlag _wrapperInitializedFlag = new InterlockedStatedFlag();

		/// <summary>
		/// Flag indicating whether a markup minification is enabled
		/// </summary>
		private bool _minificationEnabled = false;

		/// <summary>
		/// Flag indicating whether a HTTP compression is enabled
		/// </summary>
		private bool _compressionEnabled = false;

		/// <summary>
		/// Current URL
		/// </summary>
		private string _currentUrl;

		/// <summary>
		/// Text encoding
		/// </summary>
		private Encoding _encoding;

		/// <summary>
		/// Current markup minification manager
		/// </summary>
		private IMarkupMinificationManager _currentMinificationManager;

		/// <summary>
		/// Current HTTP compressor
		/// </summary>
		private ICompressor _currentCompressor;

		/// <summary>
		/// Flag indicating whether the current HTTP compressor is initialized
		/// </summary>
		private InterlockedStatedFlag _currentCompressorInitializedFlag = new InterlockedStatedFlag();

		/// <summary>
		/// Flag that indicates if the HTTP headers is modified for compression
		/// </summary>
		private InterlockedStatedFlag _httpHeadersModifiedForCompressionFlag = new InterlockedStatedFlag();


		/// <summary>
		/// Constructs an instance of the stream wrapper
		/// </summary>
		/// <param name="context">HTTP context</param>
		/// <param name="originalStream">Original stream</param>
		/// <param name="options">WebMarkupMin configuration</param>
		/// <param name="minificationManagers">List of markup minification managers</param>
		/// <param name="compressionManager">HTTP compression manager</param>
		/// <param name="bufferingFeature">HTTP buffering feature</param>
		internal BodyWrapperStream(HttpContext context, Stream originalStream,
			WebMarkupMinOptions options, IList<IMarkupMinificationManager> minificationManagers,
			IHttpCompressionManager compressionManager, IHttpBufferingFeature bufferingFeature)
		{
			_context = context;
			_originalStream = originalStream;
			_options = options;
			_minificationManagers = minificationManagers;
			_compressionManager = compressionManager;
			_bufferingFeature = bufferingFeature;
		}


		private void Initialize()
		{
			if (_wrapperInitializedFlag.Set())
			{
				HttpRequest request = _context.Request;
				HttpResponse response = _context.Response;

				if (response.StatusCode == 200)
				{
					string httpMethod = request.Method;
					string contentType = response.ContentType;
					string mediaType = null;
					Encoding encoding = null;

					if (contentType != null)
					{
						MediaTypeHeaderValue mediaTypeHeader;

						if (MediaTypeHeaderValue.TryParse(contentType, out mediaTypeHeader))
						{
							mediaType = mediaTypeHeader.MediaType
#if ASPNETCORE2
								.Value
#endif
								.ToLowerInvariant()
								;
							encoding = mediaTypeHeader.Encoding;
						}
					}

					string currentUrl = request.Path.Value;
					QueryString queryString = request.QueryString;
					if (queryString.HasValue)
					{
						currentUrl += queryString.Value;
					}

					IHeaderDictionary responseHeaders = response.Headers;
					bool isEncodedContent = responseHeaders.IsEncodedContent();

					if (_minificationManagers.Count > 0)
					{
						foreach (IMarkupMinificationManager minificationManager in _minificationManagers)
						{
							if (minificationManager.IsSupportedHttpMethod(httpMethod)
								&& mediaType != null && minificationManager.IsSupportedMediaType(mediaType)
								&& minificationManager.IsProcessablePage(currentUrl))
							{
								if (isEncodedContent)
								{
									throw new InvalidOperationException(
										string.Format(
											AspNetCommonStrings.MarkupMinificationIsNotApplicableToEncodedContent,
											responseHeaders[HeaderNames.ContentEncoding]
										)
									);
								}

								_currentMinificationManager = minificationManager;
								_cachedStream = new MemoryStream();
								_minificationEnabled = true;

								break;
							}
						}
					}

					if (_compressionManager != null && !isEncodedContent
						&& _compressionManager.IsSupportedHttpMethod(httpMethod)
						&& _compressionManager.IsSupportedMediaType(mediaType)
						&& _compressionManager.IsProcessablePage(currentUrl))
					{
						string acceptEncoding = request.Headers[HeaderNames.AcceptEncoding];
						ICompressor compressor = InitializeCurrentCompressor(acceptEncoding);

						if (compressor != null)
						{
							_compressionStream = compressor.Compress(_originalStream);
							_compressionEnabled = true;
						}
					}

					_currentUrl = currentUrl;
					_encoding = encoding;
				}
			}
		}

		private ICompressor InitializeCurrentCompressor(string acceptEncoding)
		{
			if (_currentCompressorInitializedFlag.Set())
			{
				_compressionManager?.TryCreateCompressor(acceptEncoding, out _currentCompressor);
			}

			return _currentCompressor;
		}

		private void ModifyHttpHeadersForCompressionOnce()
		{
			if (_httpHeadersModifiedForCompressionFlag.Set())
			{
				IHeaderDictionary responseHeaders = _context.Response.Headers;
				_currentCompressor.AppendHttpHeaders((key, value) =>
				{
					responseHeaders.Append(key, new StringValues(value));
				});
				responseHeaders.Remove(HeaderNames.ContentMD5);
				responseHeaders.Remove(HeaderNames.ContentLength);
			}
		}

		public async Task Finish()
		{
			if (_minificationEnabled)
			{
				byte[] cachedBytes = _cachedStream.ToArray();
				int cachedByteCount = cachedBytes.Length;

				bool isMinified = false;

				if (cachedByteCount > 0 && _options.IsAllowableResponseSize(cachedByteCount))
				{
					Encoding encoding = _encoding ?? Encoding.GetEncoding(0);
					string content = encoding.GetString(cachedBytes);

					IMarkupMinifier minifier = _currentMinificationManager.CreateMinifier();
					MarkupMinificationResult minificationResult = minifier.Minify(content, _currentUrl,
						_encoding, _currentMinificationManager.GenerateStatistics);

					if (minificationResult.Errors.Count == 0)
					{
						IHeaderDictionary responseHeaders = _context.Response.Headers;
						Action<string, string> appendHttpHeader = (key, value) =>
						{
							responseHeaders.Append(key, new StringValues(value));
						};

						if (_options.IsPoweredByHttpHeadersEnabled())
						{
							_currentMinificationManager.AppendPoweredByHttpHeader(appendHttpHeader);
						}
						responseHeaders.Remove(HeaderNames.ContentMD5);

						string processedContent = minificationResult.MinifiedContent;
						byte[] processedBytes = encoding.GetBytes(processedContent);
						int processedByteCount = processedBytes.Length;

						if (_compressionEnabled)
						{
							_currentCompressor.AppendHttpHeaders(appendHttpHeader);
							responseHeaders.Remove(HeaderNames.ContentLength);
							await _compressionStream.WriteAsync(processedBytes, 0, processedByteCount);
						}
						else
						{
							responseHeaders[HeaderNames.ContentLength] = processedByteCount.ToString();
							await _originalStream.WriteAsync(processedBytes, 0, processedByteCount);
						}

						isMinified = true;
					}

					if (!isMinified)
					{
						Stream outputStream = _compressionEnabled ? _compressionStream : _originalStream;

						_cachedStream.Seek(0, SeekOrigin.Begin);
						await _cachedStream.CopyToAsync(outputStream);
					}
				}

				_cachedStream.Clear();
			}
		}
#if NET451 || NETSTANDARD2_0

		private async void InternalWriteAsync(byte[] buffer, int offset, int count, AsyncCallback callback,
			TaskCompletionSource<object> tcs)
		{
			try
			{
				await WriteAsync(buffer, offset, count);
				tcs.TrySetResult(null);
			}
			catch (Exception ex)
			{
				tcs.TrySetException(ex);
			}

			if (callback != null)
			{
				// Offload callbacks to avoid stack dives on sync completions
				var ignored = Task.Run(() =>
				{
					try
					{
						callback(tcs.Task);
					}
					catch (Exception)
					{
						// Suppress exceptions on background threads
					}
				});
			}
		}
#endif

		#region Stream overrides

		public override long Position
		{
			get { throw new NotSupportedException(); }
			set { throw new NotSupportedException(); }
		}

		public override long Length
		{
			get { throw new NotSupportedException(); }
		}

		public override bool CanWrite
		{
			get { return _originalStream.CanWrite; }
		}

		public override bool CanSeek
		{
			get { return false; }
		}

		public override bool CanRead
		{
			get { return false; }
		}


#if NET451 || NETSTANDARD2_0
		public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback,
			object state)
		{
			var tcs = new TaskCompletionSource<object>(state);
			InternalWriteAsync(buffer, offset, count, callback, tcs);

			return tcs.Task;
		}

		public override void EndWrite(IAsyncResult asyncResult)
		{
			if (asyncResult == null)
			{
				throw new ArgumentNullException(nameof(asyncResult));
			}

			var task = (Task)asyncResult;
			task.GetAwaiter().GetResult();
		}

#endif
		public override void Flush()
		{
			Initialize();

			if (_minificationEnabled)
			{
				_cachedStream.Flush();
			}
			else if (_compressionEnabled)
			{
				_compressionStream.Flush();
			}
			else
			{
				_originalStream.Flush();
			}
		}

		public override Task FlushAsync(CancellationToken cancellationToken)
		{
			Initialize();

			if (_minificationEnabled)
			{
				return _cachedStream.FlushAsync(cancellationToken);
			}
			else if (_compressionEnabled)
			{
				return _compressionStream.FlushAsync(cancellationToken);
			}

			return _originalStream.FlushAsync(cancellationToken);
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			Initialize();

			if (_minificationEnabled)
			{
				_cachedStream.Write(buffer, offset, count);
			}
			else if (_compressionEnabled)
			{
				ModifyHttpHeadersForCompressionOnce();
				_compressionStream.Write(buffer, offset, count);
				if (_autoFlushCompressionStream)
				{
					_compressionStream.Flush();
				}
			}
			else
			{
				_originalStream.Write(buffer, offset, count);
			}
		}

		public override async Task WriteAsync(byte[] buffer, int offset, int count,
			CancellationToken cancellationToken)
		{
			Initialize();

			if (_minificationEnabled)
			{
				await _cachedStream.WriteAsync(buffer, offset, count, cancellationToken);
			}
			else if (_compressionEnabled)
			{
				ModifyHttpHeadersForCompressionOnce();
				await _compressionStream.WriteAsync(buffer, offset, count, cancellationToken);
				if (_autoFlushCompressionStream)
				{
					await _compressionStream.FlushAsync(cancellationToken);
				}
			}
			else
			{
				await _originalStream.WriteAsync(buffer, offset, count, cancellationToken);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (_compressionStream != null)
				{
					_compressionStream.Dispose();
					_compressionStream = null;
				}

				_currentCompressor = null;

				if (_cachedStream != null)
				{
					_cachedStream.Dispose();
					_cachedStream = null;
				}

				_currentMinificationManager = null;
			}

			base.Dispose(disposing);
		}

		#endregion

		#region IHttpBufferingFeature implementation

		public void DisableRequestBuffering()
		{
			_bufferingFeature?.DisableRequestBuffering();
		}

		public void DisableResponseBuffering()
		{
			string acceptEncoding = _context.Request.Headers[HeaderNames.AcceptEncoding];
			ICompressor compressor = InitializeCurrentCompressor(acceptEncoding);

			if (compressor?.SupportsFlush == false)
			{
				// Some of the compressors don't support flushing which would block real-time
				// responses like SignalR.
				_compressionEnabled = false;
				_currentCompressor = null;
			}
			else
			{
				_autoFlushCompressionStream = true;
			}

			_bufferingFeature?.DisableResponseBuffering();
		}

		#endregion
	}
}