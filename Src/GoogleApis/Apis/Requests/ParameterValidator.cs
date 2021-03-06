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

using System;
using System.Linq;
using System.Text.RegularExpressions;

using Google.Apis.Discovery;
using Google.Apis.Testing;
using Google.Apis.Util;

namespace Google.Apis
{
    /// <summary>
    /// Logic for validating a parameter
    /// </summary>
    public static class ParameterValidator
    {
        /// <summary> Validates a parameter value against the methods regex. </summary>
        [VisibleForTestOnly]
        internal static bool ValidateRegex(IParameter param, string paramValue)
        {
            return string.IsNullOrEmpty(param.Pattern) || new Regex(param.Pattern).IsMatch(paramValue);
        }

        /// <summary> Validates a parameter value against those enumeration values allowed by the method. </summary>
        [VisibleForTestOnly]
        internal static bool ValidateEnum(IParameter param, string paramValue)
        {
            // Confirm that the parameter value is contained within the enumeration
            return param.EnumValues.IsNullOrEmpty() || param.EnumValues.Contains(paramValue);
        }

        /// <summary> Validates if a parameter is valid. </summary>
        public static bool ValidateParameter(IParameter parameter, string value)
        {
            // If a required parameter is not present, fail
            if (String.IsNullOrEmpty(value))
            {
                return !parameter.IsRequired;
            }

            // The parameter is present, validate the regex
            bool isValidData = ValidateRegex(parameter, value) && ValidateEnum(parameter, value);
            if (isValidData == false)
            {
                return false;
            }

            return true;
        }
    }
}