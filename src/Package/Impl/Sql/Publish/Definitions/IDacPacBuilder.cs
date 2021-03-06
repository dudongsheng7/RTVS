﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Generic;

namespace Microsoft.VisualStudio.R.Package.Sql.Publish {
    /// <summary>
    /// DACPAC building services
    /// </summary>
    internal interface IDacPacBuilder {
        void Build(string dacpacPath, string packageName, IEnumerable<string> scripts);
    }
}
