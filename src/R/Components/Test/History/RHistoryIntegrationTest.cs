﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.R.Components.ContentTypes;
using Microsoft.R.Components.History;
using Microsoft.R.Components.InteractiveWorkflow;
using Microsoft.UnitTests.Core.Mef;
using Microsoft.UnitTests.Core.Threading;
using Microsoft.UnitTests.Core.XUnit;
using Microsoft.VisualStudio.InteractiveWindow;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.R.Components.Test.History {
    [ExcludeFromCodeCoverage]
    public class RHistoryIntegrationTest : IDisposable {
        private readonly IExportProvider _exportProvider;
        private readonly ITextBufferFactoryService _textBufferFactory;
        private readonly ITextEditorFactoryService _textEditorFactory;
        private readonly IRInteractiveWorkflowProvider _workflowProvider;
        private readonly IContentTypeRegistryService _contentTypeRegistryService;
        private readonly IInteractiveWindowComponentContainerFactory _interactiveWindowComponentContainerFactory;
        private readonly IRHistoryVisualComponentContainerFactory _historyVisualComponentContainerFactory;

        public RHistoryIntegrationTest(RComponentsMefCatalogFixture catalog) {
            _exportProvider = catalog.CreateExportProvider();
            _textBufferFactory = _exportProvider.GetExportedValue<ITextBufferFactoryService>();
            _textEditorFactory = _exportProvider.GetExportedValue<ITextEditorFactoryService>();
            _workflowProvider = _exportProvider.GetExportedValue<IRInteractiveWorkflowProvider>();
            _contentTypeRegistryService = _exportProvider.GetExportedValue<IContentTypeRegistryService>();
            _interactiveWindowComponentContainerFactory = _exportProvider.GetExportedValue<IInteractiveWindowComponentContainerFactory>();
            _historyVisualComponentContainerFactory = _exportProvider.GetExportedValue<IRHistoryVisualComponentContainerFactory>();
        }

        public void Dispose() {
            (_exportProvider as IDisposable)?.Dispose();
        }

        [Test]
        [Category.History]
        public async Task InteractiveWindowIntegration01() {
            var workflow = _workflowProvider.GetOrCreate();
            var history = workflow.History;
            var session = workflow.RSession;
            using (await UIThreadHelper.Instance.Invoke(() => workflow.GetOrCreateVisualComponent(_interactiveWindowComponentContainerFactory))) {
                workflow.ActiveWindow.Should().NotBeNull();
                session.IsHostRunning.Should().BeTrue();

                var eval = workflow.ActiveWindow.InteractiveWindow.Evaluator;
                var result = await eval.ExecuteCodeAsync("x <- c(1:10)\r\n");
                result.Should().Be(ExecutionResult.Success);
                history.HasEntries.Should().BeTrue();
                history.HasSelectedEntries.Should().BeFalse();
                history.SelectHistoryEntry(0);
                history.HasSelectedEntries.Should().BeTrue();

                var textBuffer = _textBufferFactory.CreateTextBuffer(_contentTypeRegistryService.GetContentType(RContentTypeDefinition.ContentType));
                var textView = UIThreadHelper.Instance.Invoke(() => _textEditorFactory.CreateTextView(textBuffer, _textEditorFactory.DefaultRoles));

                history.SendSelectedToTextView(textView);
                var text = textBuffer.CurrentSnapshot.GetText();
                text.Should().Be("x <- c(1:10)");

                UIThreadHelper.Instance.Invoke(() => textView.Close());
            }
        }

        [Test(ThreadType.UI)]
        [Category.History]
        public async Task InteractiveWindowIntegration02() {
            var workflow = _workflowProvider.GetOrCreate();
            var history = workflow.History;
            var session = workflow.RSession;
            using (await workflow.GetOrCreateVisualComponent(_interactiveWindowComponentContainerFactory)) {
                workflow.ActiveWindow.Should().NotBeNull();
                session.IsHostRunning.Should().BeTrue();

                history.GetOrCreateVisualComponent(_historyVisualComponentContainerFactory);
                history.VisualComponent.Should().NotBeNull();

                var eval = workflow.ActiveWindow.InteractiveWindow.Evaluator;

                var result = await eval.ExecuteCodeAsync("x <- c(1:10)\r\n");
                result.Should().Be(ExecutionResult.Success);

                result = await eval.ExecuteCodeAsync("x <- c(1:20)\r\n");
                result.Should().Be(ExecutionResult.Success);

                history.HasEntries.Should().BeTrue();
                history.HasSelectedEntries.Should().BeFalse();

                int eventCount = 0;
                history.SelectionChanged += (s, e) => {
                    eventCount++;
                };

                history.SelectHistoryEntry(1);
                history.HasSelectedEntries.Should().BeTrue();
                eventCount.Should().Be(1);

                history.SelectPreviousHistoryEntry();
                eventCount.Should().Be(3); // event fires twice?
                history.GetSelectedText().Should().Be("x <- c(1:10)");

                history.SelectNextHistoryEntry();
                eventCount.Should().Be(5);
                history.GetSelectedText().Should().Be("x <- c(1:20)");

                var interactiveWindowTextBuffer = workflow.ActiveWindow.CurrentLanguageBuffer;
                history.PreviousEntry();
                interactiveWindowTextBuffer.CurrentSnapshot.GetText().Should().Be("x <- c(1:20)");

                history.PreviousEntry();
                interactiveWindowTextBuffer.CurrentSnapshot.GetText().Should().Be("x <- c(1:10)");

                history.NextEntry();
                interactiveWindowTextBuffer.CurrentSnapshot.GetText().Should().Be("x <- c(1:20)");

                history.SelectHistoryEntries(new[] { 0, 1 });
                history.GetSelectedText().Should().Be("x <- c(1:10)\r\nx <- c(1:20)");
                eventCount.Should().Be(6);

                history.ToggleHistoryEntrySelection(1);
                history.GetSelectedText().Should().Be("x <- c(1:10)");
                eventCount.Should().Be(7);

                history.DeselectHistoryEntry(0);
                history.HasSelectedEntries.Should().BeFalse();
                eventCount.Should().Be(8);

                history.SelectAllEntries();
                history.HasSelectedEntries.Should().BeTrue();
                string text = history.GetSelectedText();
                text.Should().Be("x <- c(1:10)\r\nx <- c(1:20)");

                var spans = history.GetSelectedHistoryEntrySpans();
                spans.Count.Should().Be(1);

                spans[0].Start.Position.Should().Be(0);
                spans[0].End.Position.Should().Be(26);

                history.DeselectHistoryEntry(0);
                history.DeselectHistoryEntry(1);
                history.HasSelectedEntries.Should().BeFalse();

                history.SelectAllEntries();
                history.ToggleHistoryEntrySelection(1);

                history.DeleteSelectedHistoryEntries();
                history.SelectAllEntries();

                text = history.GetSelectedText();
                text.Should().Be("x <- c(1:20)");

                history.DeleteAllHistoryEntries();

                history.HasEntries.Should().BeFalse();
                history.HasSelectedEntries.Should().BeFalse();

                text = history.GetSelectedText();
                text.Should().Be(string.Empty);
            }
        }
    }
}
