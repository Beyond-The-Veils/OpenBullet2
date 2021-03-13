﻿using System;
using System.IO;
using System.Net;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Net.Sockets;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Collections.Generic;
using RuriLib.Http.Helpers;
using RuriLib.Http;
using System.IO.Pipelines;
using System.Buffers;

namespace RuriLib.Http
{
    internal class HttpResponseMessageBuilder
    {
        private PipeReader reader;
        private const string newLine = "\r\n";
        private readonly byte[] CRLF = Encoding.UTF8.GetBytes(newLine);
        private static byte[] CRLFCRLF_Bytes = { 13, 10, 13, 10 };

        private int contentLength = -1;

        private NetworkStream networkStream;
        private Stream commonStream;

        private HttpResponseMessage response;
        private Dictionary<string, List<string>> contentHeaders;

        private readonly CookieContainer cookies;
        private readonly Uri uri;

        //  private readonly ReceiveHelper receiveHelper;

        public TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromSeconds(10);

        public HttpResponseMessageBuilder(int bufferSize, CookieContainer cookies = null, Uri uri = null)
        {
            //  this.bufferSize = bufferSize;
            this.cookies = cookies;
            this.uri = uri;

            //  receiveHelper = new ReceiveHelper(bufferSize);
        }

        public async Task<HttpResponseMessage> GetResponseAsync(HttpRequestMessage request, Stream stream,
            CancellationToken cancellationToken = default)
        {
            networkStream = stream as NetworkStream;
            commonStream = stream;
           
            reader = PipeReader.Create(stream);
            
            response = new HttpResponseMessage();
            contentHeaders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            response.RequestMessage = request;

            await ReceiveFirstLineAsync(cancellationToken).ConfigureAwait(false);
            await ReceiveHeadersAsync(cancellationToken).ConfigureAwait(false);
            await ReceiveContentAsync(cancellationToken).ConfigureAwait(false);

            return response;
        }

        // Parses the first line, for example
        // HTTP/1.1 200 OK
        private async Task ReceiveFirstLineAsync(CancellationToken cancellationToken = default)
        {
            var startingLine = string.Empty;

            // Read the first line from the Network Stream
            while (true)
            {
                var res = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                
                var buff = res.Buffer;
                int crlfIndex = buff.FirstSpan.IndexOf(CRLF);
                if (crlfIndex > -1)
                {
                    try
                    {
                        startingLine = Encoding.UTF8.GetString(buff.FirstSpan.Slice(0, crlfIndex));
                        var fields = startingLine.Split(' ');
                        response.Version = Version.Parse(fields[0].Trim()[5..]);
                        response.StatusCode = (HttpStatusCode)Enum.Parse(typeof(HttpStatusCode), fields[1]);
                        buff = buff.Slice(0, crlfIndex + 2); // add 2 bytes for the CRLF
                        reader.AdvanceTo(buff.End); // advance to the consumed position
                        break;
                    }
                    catch
                    {
                        throw new FormatException($"Invalid first line of the HTTP response: {startingLine}");
                    }
                }
                else
                {
                    // the responce is incomplete ex. (HTTP/1.1 200 O)
                    reader.AdvanceTo(buff.Start, buff.End); // nothing consumed but all the buffer examined loop and read more.
                }
                if (res.IsCanceled || res.IsCompleted)
                {
                    reader.Complete();
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }


        }

        // Parses the headers
        private async Task ReceiveHeadersAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var res = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
              
                var buff = res.Buffer;
                if (buff.IsSingleSegment)
                {
                    if (ReadHeadersFastPath(ref buff))
                    {
                        reader.AdvanceTo(buff.Start, buff.End);
                        break;
                    }

                }
                else
                {
                    if (ReadHeadersSlowerPath(ref buff))
                    {
                        reader.AdvanceTo(buff.Start, buff.End);
                        break;
                    }
                }
                reader.AdvanceTo(buff.Start, buff.End);// not adding ghis linw might result in infinit loop
                if (res.IsCanceled || res.IsCompleted)
                {
                    reader.Complete();
                    cancellationToken.ThrowIfCancellationRequested();
                }

            }
        }
        /// <summary>
        /// Reads all Header Lines using Span<T> For High Perfromace Parsing.
        /// </summary>
        /// <param name="buff">Buffered Data From Pipe</param>
        private bool ReadHeadersFastPath(ref ReadOnlySequence<byte> buff)
        {
            int endofheadersindex;
            if ((endofheadersindex = buff.FirstSpan.IndexOf(CRLFCRLF_Bytes)) > -1)
            {
                var spanLines = buff.FirstSpan.Slice(0, endofheadersindex + 4);
                var Lines = spanLines.SplitLines();// we use spanHelper class here to make a for each loop.
                foreach (var Line in Lines)
                {
                    string HeaderLine = Encoding.UTF8.GetString(Line);
                    ProcessHeaderLine(HeaderLine);
                }

                buff = buff.Slice(endofheadersindex + 4); // add 4 bytes for \r\n\r\n and to advance the pipe back in the calling method
                return true;
            }
            return false;
        }
        /// <summary>
        /// Reads all Header Lines using SequenceReader.
        /// </summary>
        /// <param name="buff">Buffered Data From Pipe</param>
        private bool ReadHeadersSlowerPath(ref ReadOnlySequence<byte> buff)
        {
            var reader = new SequenceReader<byte>(buff);

            while (reader.TryReadTo(out ReadOnlySpan<byte> Line, CRLF, true))
            {
                if (Line.Length == 0)// reached last crlf (empty line)
                {
                    buff = buff.Slice(reader.Position);
                    return true;// all headers received
                }
                ProcessHeaderLine(Encoding.UTF8.GetString(Line));
            }
            buff = buff.Slice(reader.Position);
            return false;// empty line not found need more data
        }

        private void ProcessHeaderLine(string header)
        {
            if (String.IsNullOrEmpty(header))
            {
                return;
            }
            var separatorPos = header.IndexOf(':');

            var headerName = header.Substring(0, separatorPos);
            var headerValue = header[(separatorPos + 1)..].Trim(' ', '\t', '\r', '\n');

            // If the header is Set-Cookie, add the cookie
            if (headerName.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ||
                headerName.Equals("Set-Cookie2", StringComparison.OrdinalIgnoreCase))
            {
                SetCookie(headerValue);
            }
            // If it's a content header
            else if (ContentHelper.IsContentHeader(headerName))
            {
                if (contentHeaders.TryGetValue(headerName, out var values))
                {
                    values.Add(headerValue);
                }
                else
                {
                    values = new List<string>
                        {
                            headerValue
                        };

                    contentHeaders.Add(headerName, values);
                }
            }
            else
            {
                response.Headers.TryAddWithoutValidation(headerName, headerValue);
            }
        }
        // Sets the value of a cookie
        private void SetCookie(string value)
        {
            if (value.Length == 0)
            {
                return;
            }

            var endCookiePos = value.IndexOf(';');
            var separatorPos = value.IndexOf('=');

            if (separatorPos == -1)
            {
                throw new FormatException($"Invalid cookie format: {value}");
            }

            string cookieValue;
            var cookieName = value.Substring(0, separatorPos);

            if (endCookiePos == -1)
            {
                cookieValue = value[(separatorPos + 1)..];
            }
            else
            {
                cookieValue = value.Substring(separatorPos + 1, (endCookiePos - separatorPos) - 1);

                #region Get Expiration Time

                var expiresPos = value.IndexOf("expires=");

                if (expiresPos != -1)
                {
                    string expiresStr;
                    var endExpiresPos = value.IndexOf(';', expiresPos);

                    expiresPos += 8;

                    if (endExpiresPos == -1)
                    {
                        expiresStr = value[expiresPos..];
                    }
                    else
                    {
                        expiresStr = value[expiresPos..endExpiresPos];
                    }

                    if (DateTime.TryParse(expiresStr, out var expires) &&
                        expires < DateTime.Now)
                    {
                        var collection = cookies.GetCookies(uri);
                        if (collection[cookieName] != null)
                            collection[cookieName].Expired = true;
                    }
                }

                #endregion
            }

            if (cookieValue.Length == 0 ||
                cookieValue.Equals("deleted", StringComparison.OrdinalIgnoreCase))
            {
                var collection = cookies.GetCookies(uri);
                if (collection[cookieName] != null)
                    collection[cookieName].Expired = true;
            }
            else
            {
                cookies.Add(new Cookie(cookieName, cookieValue, "/", uri.Host));
            }
        }

        // TODO: Make this async (need to refactor the mess below)
        private async Task ReceiveContentAsync(CancellationToken cancellationToken = default)
        {
            // If there are content headers
            if (contentHeaders.Count != 0)
            {
                contentLength = GetContentLength();

                // Try to get the body and write it to a MemoryStream
                var finaleResponceStream = await GetMessageBodySource(cancellationToken).ConfigureAwait(false);
                // Rewind the stream and set the content of the response and its headers
                finaleResponceStream.Seek(0, SeekOrigin.Begin);
                response.Content = new StreamContent(finaleResponceStream);
                foreach (var pair in contentHeaders)
                {
                    response.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                }
            }
        }

        private Task<Stream> GetMessageBodySource(CancellationToken cancellationToken)
        {
            if (response.Headers.Contains("Transfer-Encoding"))
            {
                if (contentHeaders.ContainsKey("Content-Encoding"))
                {
                    return GetChunkedDecompressedStream(cancellationToken);
                }
                else
                {
                    return ReceiveMessageBodyChunked(cancellationToken);
                }
            }
            else //if (contentLength > -1)
            {
                if (contentHeaders.ContainsKey("Content-Encoding"))
                {
                    return GetContentLengthDecompressedStream(cancellationToken);
                }
                else
                {
                    return ReciveContentLength(cancellationToken);

                }
            }

        }
        private async Task<Stream> GetContentLengthDecompressedStream(CancellationToken cancellationToken)
        {
            using (var compressedStream = GetZipStream(await ReciveContentLength(cancellationToken).ConfigureAwait(false)))
            {
                var decompressedStream = new MemoryStream();
                await compressedStream.CopyToAsync(decompressedStream, cancellationToken);
                return decompressedStream;
            }
        }

        private async Task<Stream> GetChunkedDecompressedStream(CancellationToken cancellationToken)
        {
            using (var compressedStream = GetZipStream(await ReceiveMessageBodyChunked(cancellationToken).ConfigureAwait(false)))
            {
                var decompressedStream = new MemoryStream();
                await compressedStream.CopyToAsync(decompressedStream, cancellationToken).ConfigureAwait(false);
                return decompressedStream;
            }
        }

        private async Task<Stream> ReciveContentLength(CancellationToken cancellationToken)
        {
            MemoryStream contentlenghtStream = new MemoryStream(contentLength);

            while (true)
            {
                var res = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

                var buff = res.Buffer;
                if (buff.IsSingleSegment)
                {
                    contentlenghtStream.Write(buff.FirstSpan);
                }
                else
                {
                    foreach (var seg in buff)
                    {
                        contentlenghtStream.Write(seg.Span);
                    }
                }
                reader.AdvanceTo(buff.End);

                if (contentlenghtStream.Length >= contentLength)
                {
                    return contentlenghtStream;
                }

                if (res.IsCanceled || res.IsCompleted)
                {
                    reader.Complete();
                    cancellationToken.ThrowIfCancellationRequested();
                }

            }
        }




        private int GetContentLength()
        {
            if (contentHeaders.TryGetValue("Content-Length", out var values))
            {
                if (int.TryParse(values[0], out var length))
                {
                    return length;
                }
            }

            return -1;
        }

        private string GetContentEncoding()
        {
            var encoding = "";

            if (contentHeaders.TryGetValue("Content-Encoding", out var values))
            {
                encoding = values[0];
            }

            return encoding;
        }



      
        private async Task<Stream> ReceiveMessageBodyChunked(CancellationToken cancellationToken)
        {
            var chunkedDecoder = new ChunkedDecoderOptimized();
            while (true)
            {
                var res = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

                var buff = res.Buffer;
                chunkedDecoder.Decode(ref buff);
                reader.AdvanceTo(buff.Start, buff.End);
                if (chunkedDecoder.Finished)
                {
                    return chunkedDecoder.DecodedStream;
                }
                if (res.IsCanceled || res.IsCompleted)
                {
                    reader.Complete();
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }





        private Stream GetZipStream(Stream stream)
        {
            var contentEncoding = GetContentEncoding().ToLower();
            stream.Seek(0, SeekOrigin.Begin);
            return contentEncoding switch
            {
                "gzip" => new GZipStream(stream, CompressionMode.Decompress, false),
                "deflate" => new DeflateStream(stream, CompressionMode.Decompress, false),
                "br" => new BrotliStream(stream, CompressionMode.Decompress, false),
                _ => throw new InvalidOperationException($"'{contentEncoding}' not supported encoding format"),
            };
        }




    }
}
