// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
// xc-osu! web port (xc softworks, kudasaixc)

using System;
using System.Net;

namespace osu.Framework.IO.Network
{
    /// <summary>
    /// Browser stand-in for <see cref="WebException"/>, whose constructors throw <see cref="PlatformNotSupportedException"/>
    /// on WebAssembly (System.Net.Requests is not supported there).
    /// </summary>
    public class RequestFailedException : Exception
    {
        public WebExceptionStatus Status { get; }

        public RequestFailedException(string message, WebExceptionStatus status = WebExceptionStatus.UnknownError)
            : base(message)
        {
            Status = status;
        }

        /// <summary>A <see cref="WebException"/> on platforms that support it, a <see cref="RequestFailedException"/> otherwise.</summary>
        public static Exception Create(string message, WebExceptionStatus status = WebExceptionStatus.UnknownError)
            => RuntimeInfo.IsBrowser ? new RequestFailedException(message, status) : new WebException(message, status);

        /// <summary>Matches both <see cref="WebException"/> and <see cref="RequestFailedException"/>.</summary>
        public static bool TryGet(Exception? e, out string message, out WebExceptionStatus status)
        {
            switch (e)
            {
                case RequestFailedException r:
                    message = r.Message;
                    status = r.Status;
                    return true;

                case WebException w:
                    message = w.Message;
                    status = w.Status;
                    return true;

                default:
                    message = string.Empty;
                    status = default;
                    return false;
            }
        }
    }
}
