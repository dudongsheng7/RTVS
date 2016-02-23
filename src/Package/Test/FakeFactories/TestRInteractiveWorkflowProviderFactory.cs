﻿using Microsoft.Common.Core.Shell;
using Microsoft.R.Components.ContentTypes;
using Microsoft.R.Components.History;
using Microsoft.R.Components.InteractiveWorkflow;
using Microsoft.R.Components.Settings;
using Microsoft.R.Components.Test.Fakes.InteractiveWindow;
using Microsoft.R.Components.Test.StubFactories;
using Microsoft.R.Host.Client;
using Microsoft.R.Host.Client.Mocks;
using Microsoft.R.Support.Settings;
using Microsoft.VisualStudio.R.Package.Shell;
using Microsoft.VisualStudio.R.Package.Test.Mocks;
using Microsoft.VisualStudio.R.Package.Utilities;

namespace Microsoft.VisualStudio.R.Package.Test.FakeFactories {
    public static class TestRInteractiveWorkflowProviderFactory {
        public static IRInteractiveWorkflowProvider Create(IRSessionProvider sessionProvider = null
            , IRHistoryProvider historyProvider = null
            , IInteractiveWindowComponentContainerFactory componentContainerFactory = null
            , IActiveWpfTextViewTracker activeTextViewTracker = null
            , IDebuggerModeTracker debuggerModeTracker = null
            , ICoreShell shell = null
            , IRSettings settings = null) {
            sessionProvider = sessionProvider ?? new RSessionProviderMock();
            historyProvider = historyProvider ?? RHistoryProviderStubFactory.CreateDefault();
            componentContainerFactory = componentContainerFactory ?? new InteractiveWindowComponentContainerFactoryMock();

            activeTextViewTracker = activeTextViewTracker ?? new ActiveTextViewTrackerMock(string.Empty, RContentTypeDefinition.ContentType);
            debuggerModeTracker = debuggerModeTracker ?? new VsDebuggerModeTracker();

           return new TestRInteractiveWorkflowProvider(sessionProvider, historyProvider, componentContainerFactory, activeTextViewTracker, debuggerModeTracker, shell ?? VsAppShell.Current, settings ?? RToolsSettings.Current);
        }
    }
}