/*
Copyright 2010 Google Inc

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/
/* Change history
* Oct 13 2008  Joe Feser       joseph.feser@gmail.com
* Converted List<object>s and other .NET 1.1 collections to use Generics
* Combined IExtensionElement and IExtensionElementFactory interfaces
* 
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Reflection;

using Google.Apis.Util;

namespace Google.Apis
{
    /// <summary> String utilities. </summary>
    public static class Utilities
    {
        /// <summary> True when the Mono-runtime is used to execute this code. </summary>
        public static readonly bool IsRunningOnMono = (Type.GetType("Mono.Runtime") != null);

        /// <summary> xsd version of bool:true. </summary>
        public const string XSDTrue = "true";

        /// <summary> xsd version of bool:false. </summary>
        public const string XSDFalse = "false";

        /// <summary>returns a blank emptyDate. That's the default for an empty string date</summary> 
        public static DateTime EmptyDate
        {
            get
            {
                // that's the blank value you get when setting a DateTime to an empty string inthe property browswer
                return new DateTime(1, 1, 1);
            }
        }

        /// <summary> Helper to read in a string and Encode it. </summary> 
        /// <param name="content">the xmlreader string</param>
        /// <returns>UTF8 encoded string</returns>
        public static string EncodeString(string content)
        {
            // get the encoding
            Encoding utf8Encoder = Encoding.UTF8;

            Byte[] utf8Bytes = EncodeStringToUtf8(content);

            char[] utf8Chars = new char[utf8Encoder.GetCharCount(utf8Bytes, 0, utf8Bytes.Length)];
            utf8Encoder.GetChars(utf8Bytes, 0, utf8Bytes.Length, utf8Chars, 0);

            String utf8String = new String(utf8Chars);

            return utf8String;
        }

        /// <summary>
        /// returns you a bytearray of UTF8 bytes from the string passed in
        /// the passed in string is assumed to be UTF16
        /// </summary>
        /// <param name="content">UTF16 string</param>
        /// <returns>utf 8 byte array</returns>
        public static Byte[] EncodeStringToUtf8(string content)
        {
            // get the encoding
            Encoding utf8Encoder = Encoding.UTF8;
            Encoding utf16Encoder = Encoding.Unicode;

            Byte[] bytes = utf16Encoder.GetBytes(content);

            Byte[] utf8Bytes = Encoding.Convert(utf16Encoder, utf8Encoder, bytes);
            return utf8Bytes;
        }

        /// <summary>helper to read in a string and Encode it according to 
        /// RFC 5023 rules for slugheaders</summary> 
        /// <param name="slug">the Unicode string for the slug header</param>
        /// <returns>ASCII  encoded string</returns>
        public static string EncodeSlugHeader(string slug)
        {
            if (slug == null)
            {
                return "";
            }

            Byte[] bytes = EncodeStringToUtf8(slug);

            if (bytes == null)
            {
                return "";
            }

            StringBuilder returnString = new StringBuilder(256);

            foreach (byte b in bytes)
            {
                if ((b < 0x20) || (b == 0x25) || (b > 0x7E))
                {
                    returnString.AppendFormat(CultureInfo.InvariantCulture, "%{0:X}", b);
                }
                else
                {
                    returnString.Append((char)b);
                }
            }

            return returnString.ToString();
        }

        /// <summary>
        ///  tests an etag for weakness. returns TRUE for weak etags and for null strings
        /// </summary>
        /// <param name="eTag"></param>
        /// <returns></returns>
        public static bool IsWeakETag(string eTag)
        {
            if (eTag == null)
            {
                return true;
            }
            if (eTag.StartsWith("W/"))
            {
                return true;
            }
            return false;
        }

        /// <summary>Method to output just the date portion as string</summary>
        /// <param name="dateTime">the DateTime object to output as a string</param>
        /// <returns>an rfc-3339 string</returns>
        public static string LocalDateInUTC(DateTime dateTime)
        {
            // Add "full-date T partial-time"
            return dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>Helper method to format a TimeSpan as a string compliant with the "time-offset" format defined in RFC-3339</summary>
        /// <param name="spanFromUtc">the TimeSpan to format</param>
        /// <returns></returns>
        public static string FormatTimeOffset(TimeSpan spanFromUtc)
        {
            // Simply return "Z" if there is no offset
            if (spanFromUtc == TimeSpan.Zero)
            {
                return "Z";
            }

            // Return the numeric offset
            TimeSpan absoluteSpan = spanFromUtc.Duration();
            if (spanFromUtc > TimeSpan.Zero)
            {
                return "+" + FormatNumOffset(absoluteSpan);
            }
            else
            {
                return "-" + FormatNumOffset(absoluteSpan);
            }
        }

        /// <summary>Helper method to format a TimeSpan to {HH}:{MM}</summary>
        /// <param name="timeSpan">the TimeSpan to format</param>
        /// <returns>a string in "hh:mm" format.</returns>
        internal static string FormatNumOffset(TimeSpan timeSpan)
        {
            return String.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", timeSpan.Hours, timeSpan.Minutes);
        }

        /////////////////////////////////////////////////////////////////////////////

        /// <summary> Returns the version of the Core library. </summary>
        public static string GetLibraryVersion()
        {
            return Regex.Match(typeof(Utilities).Assembly.FullName, "Version=([\\d\\.]+)").Groups[1].ToString();
        }

        /// <summary>
        /// Replaces all the specified characters within the input string with the given replacement
        /// </summary>
        public static string Replace(this string input, string replace, params char[] invalidCharacters)
        {
            invalidCharacters.ThrowIfNullOrEmpty("invalidCharacters");

            // Create the resulting string
            var result = new StringBuilder(input.Length);

            foreach (char c in input)
            {
                // Replace invalid characters with the replacement string
                if (invalidCharacters.Contains(c))
                {
                    result.Append(replace);
                    continue;
                }

                result.Append(c);
            }

            return result.ToString();
        }

        #region LINQ extensions

        /// <summary>
        /// Returns the enumerable with the specified element removed
        /// </summary>
        public static IEnumerable<T> Without<T>(this IEnumerable<T> enumerable, T toRemove)
        {
            return enumerable.Except(new[] { toRemove });
        }

        /// <summary>
        /// Returns the enumerable with the specified element added to the end of it.
        /// </summary>
        public static IEnumerable<T> Concat<T>(this IEnumerable<T> enumerable, T toAdd)
        {
            return enumerable.Concat(new[] { toAdd });
        }

        #endregion
    }
}