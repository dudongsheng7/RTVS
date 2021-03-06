﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Threading.Tasks;

namespace Microsoft.R.Host.Client {
    public interface IObjectViewer {
        Task ViewObjectDetails(IRSession session, string environmentExpression, string expression, string title);
        Task ViewFile(string fileName, string tabName, bool deleteFile);
    }
}
