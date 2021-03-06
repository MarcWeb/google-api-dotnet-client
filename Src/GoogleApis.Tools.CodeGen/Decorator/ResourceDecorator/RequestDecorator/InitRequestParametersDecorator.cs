﻿/*
Copyright 2013 Google Inc

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

using System.CodeDom;

using Google.Apis.Discovery;

namespace Google.Apis.Tools.CodeGen.Decorator.ResourceDecorator.RequestDecorator
{
    /// <summary>
    /// Generates InitParameters method which intializes <code>RequestParamters</code> 
    /// by the request (IMethod) specific parameters.
    /// </summary>
    public class InitRequestParametersDecorator : IRequestDecorator
    {
        internal const string ParametersName = "_requestParameters";

        public void DecorateClass(IResource resource, IMethod request, CodeTypeDeclaration requestClass,
            CodeTypeDeclaration resourceClass)
        {
            var method = new CodeMemberMethod();

            // Generate: private void InitParameters()
            method.Name = "InitParameters";
            method.ReturnType = new CodeTypeReference(typeof(void));
            method.Attributes = MemberAttributes.Private;

            // Add request parameters initialization
            DecoratorUtil.AddInitializeParameters(method, ParametersName, request.Parameters);

            requestClass.Members.Add(method);
        }
    }
}
