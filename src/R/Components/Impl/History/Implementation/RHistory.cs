﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.Common.Core;
using Microsoft.Common.Core.Disposables;
using Microsoft.Common.Core.IO;
using Microsoft.R.Components.Extensions;
using Microsoft.R.Components.InteractiveWorkflow;
using Microsoft.R.Components.Settings;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Operations;

namespace Microsoft.R.Components.History.Implementation {
    internal sealed class RHistory : IRHistory {
        private const string BlockSeparator = "\r\n";
        private const string LineSeparator = "\u00a0";

        private readonly IFileSystem _fileSystem;
        private readonly IEditorOperationsFactoryService _editorOperationsFactory;
        private readonly IRInteractiveWorkflow _interactiveWorkflow;
        private readonly ITextBuffer _historyTextBuffer;
        private readonly CountdownDisposable _textBufferIsEditable;
        private readonly IRtfBuilderService _rtfBuilderService;
        private readonly Action _dispose;

        private ITextSelection _textViewSelection;
        private IEditorOperations _editorOperations;

        private IRHistoryEntries _entries;
        private IReadOnlyRegion _readOnlyRegion;
        private IRHistoryEntry _currentEntry;
        private bool _isMultiline;

        public IRHistoryWindowVisualComponent VisualComponent { get; private set; }

        public event EventHandler<EventArgs> HistoryChanging;
        public event EventHandler<EventArgs> HistoryChanged;
        public event EventHandler<EventArgs> SelectionChanged;

        public RHistory(IRInteractiveWorkflow interactiveWorkflow, ITextBuffer textBuffer, IFileSystem fileSystem, IRSettings settings, IEditorOperationsFactoryService editorOperationsFactory, IRtfBuilderService rtfBuilderService, Action dispose) {
            _interactiveWorkflow = interactiveWorkflow;
            _historyTextBuffer = textBuffer;
            _fileSystem = fileSystem;
            _editorOperationsFactory = editorOperationsFactory;
            _rtfBuilderService = rtfBuilderService;
            _dispose = dispose;

            _textBufferIsEditable = new CountdownDisposable(MakeTextBufferReadOnly);
            _isMultiline = settings.MultilineHistorySelection;

            if (_isMultiline) {
                _entries = new MultilineRHistoryEntries();
            } else {
                _entries = new SinglelineRHistoryEntries();
            }

            MakeTextBufferReadOnly();
        }

        public bool HasEntries => _entries.HasEntries;
        public bool HasSelectedEntries => _entries.HasSelectedEntries;

        public bool IsMultiline {
            get { return _isMultiline; }
            set {
                if (value == _isMultiline) {
                    return;
                }

                var snapshot = _historyTextBuffer.CurrentSnapshot;
                if (value) {
                    _entries = new MultilineRHistoryEntries(_entries);
                } else {
                    _entries = new SinglelineRHistoryEntries(_entries);
                }
                
                if (_currentEntry != null) {
                    var currentEntryStart = _currentEntry.Span.GetStartPoint(snapshot);
                    _currentEntry = _entries.Find(e => e.Span.GetSpan(snapshot).Contains(currentEntryStart));
                }

                _isMultiline = value;
                OnSelectionChanged();
            }
        }


        public IRHistoryWindowVisualComponent GetOrCreateVisualComponent(IRHistoryVisualComponentContainerFactory visualComponentContainerFactory, int instanceId = 0) { 
            if (VisualComponent != null) {
                return VisualComponent;
            }

            VisualComponent = visualComponentContainerFactory.GetOrCreate(_historyTextBuffer, instanceId).Component;
            _textViewSelection = VisualComponent.TextView.Selection;
            _editorOperations = _editorOperationsFactory.GetEditorOperations(VisualComponent.TextView);
            return VisualComponent;
        }

        public bool TryLoadFromFile(string path) {
            string[] historyLines;
            try {
                historyLines = _fileSystem.FileReadAllLines(path).ToArray();
            } catch (Exception) {
                // .RHistory file isn't mandatory for r session, so if it can't be loaded, just exit
                return false;
            }

            var raiseEvent = _entries.HasSelectedEntries;

            DeleteAllEntries();
            try {
                CreateEntries(historyLines);
            } catch (Exception) {
                // Don't crash if history file is corrupted. Just exit.
                return false;
            }

            if (raiseEvent) {
                OnSelectionChanged();
            }

            return true;
        }

        public bool TrySaveToFile(string path) {
            var snapshot = _historyTextBuffer.CurrentSnapshot;
            var content = _entries.GetEntries().Select(e => e.Span.GetText(snapshot).Replace(BlockSeparator, LineSeparator)).ToArray();
            try {
                _fileSystem.FileWriteAllLines(path, content);
                return true;
            } catch (Exception) {
                // Failure to save .RHistory isn't critical
                return false;
            }
        }

        public void SendSelectedToRepl() {
            if (_interactiveWorkflow.ActiveWindow == null) {
                return;
            }

            var selectedText = GetSelectedText();
            _interactiveWorkflow.ActiveWindow.Container.Show(true);
            _interactiveWorkflow.Operations.ReplaceCurrentExpression(selectedText);
            _interactiveWorkflow.Operations.PositionCaretAtPrompt();
        }

        public void SendSelectedToTextView(ITextView textView) {
            var targetTextViewEditorOperations = _editorOperationsFactory.GetEditorOperations(textView);
            var selectedText = GetSelectedText();
            if (textView.Selection.IsEmpty) {
                targetTextViewEditorOperations.InsertText(selectedText);
            } else if (textView.Selection.Mode == TextSelectionMode.Box) {
                VirtualSnapshotPoint _, __;
                targetTextViewEditorOperations.InsertTextAsBox(selectedText, out _, out __);
            } else {
                targetTextViewEditorOperations.ReplaceSelection(selectedText);
            }
        }

        public void PreviousEntry() {
            if (!HasEntries) {
                return;
            }

            if (_currentEntry == null) {
                _currentEntry = _entries.Last();
            } else while (_currentEntry.Previous != null) {
                _currentEntry = _currentEntry.Previous;
                if (!_historyTextBuffer.IsContentEqualsOrdinal(_currentEntry.Next.Span, _currentEntry.Span)) {
                    break;
                }
            }

            if (_currentEntry != null) {
                _interactiveWorkflow.Operations.ReplaceCurrentExpression(_currentEntry.Span.GetText(_historyTextBuffer.CurrentSnapshot));
            }
        }

        public void NextEntry() {
            if (_currentEntry == null) {
                return;
            }

            while (_currentEntry.Next != null) {
                _currentEntry = _currentEntry.Next;
                if (!_historyTextBuffer.IsContentEqualsOrdinal(_currentEntry.Previous.Span, _currentEntry.Span)) {
                    break;
                }
            }

            if (_currentEntry != null) {
                _interactiveWorkflow.Operations.ReplaceCurrentExpression(_currentEntry.Span.GetText(_historyTextBuffer.CurrentSnapshot));
            }
        }

        public void CopySelection() {
            if (VisualComponent == null) {
                return;
            }

            if (!HasSelectedEntries) {
                _editorOperations.CopySelection();
                return;
            }

            var selectedEntries = GetSelectedHistoryEntrySpans();
            var normalizedCollection = new NormalizedSnapshotSpanCollection(selectedEntries);
            var text = GetSelectedText();
            var rtf = _rtfBuilderService.GenerateRtf(normalizedCollection, VisualComponent.TextView);
            var data = new DataObject();
            data.SetText(text, TextDataFormat.Text);
            data.SetText(text, TextDataFormat.UnicodeText);
            data.SetText(rtf, TextDataFormat.Rtf);
            data.SetData(DataFormats.StringFormat, text);
            Clipboard.SetDataObject(data, false);
        }

        public void ScrollToTop() {
            var snapshotPoint = new SnapshotPoint(_historyTextBuffer.CurrentSnapshot, 0);
            VisualComponent?.TextView.DisplayTextLineContainingBufferPosition(snapshotPoint, 0, ViewRelativePosition.Top);
        }

        public void ScrollPageUp() {
            _editorOperations.ScrollPageUp();
        }

        public void ScrollPageDown() {
            _editorOperations.ScrollPageDown();
        }

        public void ScrollToBottom() {
            var snapshotPoint = new SnapshotPoint(_historyTextBuffer.CurrentSnapshot, _historyTextBuffer.CurrentSnapshot.Length);
            VisualComponent?.TextView.DisplayTextLineContainingBufferPosition(snapshotPoint, 0, ViewRelativePosition.Top);
        }

        public IReadOnlyList<SnapshotSpan> GetAllHistoryEntrySpans() {
            if (!HasEntries) {
                return new List<SnapshotSpan>();
            }

            var snapshot = _historyTextBuffer.CurrentSnapshot;
            return _entries.GetEntries().Select(e => e.Span.GetSpan(snapshot)).ToList();
        }

        public IReadOnlyList<SnapshotSpan> GetSelectedHistoryEntrySpans() {
            var snapshotSpans = new List<SnapshotSpan>();
            if (!HasSelectedEntries) {
                return snapshotSpans;
            }

            var snapshot = _historyTextBuffer.CurrentSnapshot;
            var start = new SnapshotPoint(snapshot, 0);

            foreach (var entry in _entries.GetSelectedEntries()) {
                if (entry.Previous == null || !entry.Previous.IsSelected) {
                    start = entry.Span.GetStartPoint(snapshot);
                }

                if (entry.Next == null || !entry.Next.IsSelected) {
                    var end = entry.Span.GetEndPoint(snapshot);
                    if (start != end) {
                        snapshotSpans.Add(new SnapshotSpan(start, end));
                    }
                }
            }

            return snapshotSpans;
        }

        public string GetSelectedText() {
            if (HasSelectedEntries) {
                var snapshot = _historyTextBuffer.CurrentSnapshot;
                return string.Join(BlockSeparator, _entries.GetSelectedEntries().Select(e => e.Span.GetText(snapshot)));
            }

            if (_textViewSelection == null || _textViewSelection.IsEmpty) {
                return string.Empty;
            }

            return string.Join(BlockSeparator, _textViewSelection.SelectedSpans.Select(s => s.GetText()));
        }

        public SnapshotSpan SelectHistoryEntry(int lineNumber) {
            var entry = GetHistoryEntryFromLineNumber(lineNumber);
            if (!entry.IsSelected) {
                entry.IsSelected = true;
                OnSelectionChanged();
            }

            return entry.Span.GetSpan(_historyTextBuffer.CurrentSnapshot);
        }

        public void SelectHistoryEntries(IEnumerable<int> lineNumbers) {
            var entriesToSelect = lineNumbers
                .Select(GetHistoryEntryFromLineNumber)
                .Where(entry => !entry.IsSelected)
                .ToList();

            foreach (var entry in entriesToSelect) {
                entry.IsSelected = true;
            }

            if (entriesToSelect.Any()) {
                OnSelectionChanged();
            }
        }

        public SnapshotSpan DeselectHistoryEntry(int lineNumber) {
            var entry = GetHistoryEntryFromLineNumber(lineNumber);
            if (entry.IsSelected) {
                entry.IsSelected = false;
                OnSelectionChanged();
            }

            return entry.Span.GetSpan(_historyTextBuffer.CurrentSnapshot);
        }

        public SnapshotSpan ToggleHistoryEntrySelection(int lineNumber) {
            var entry = GetHistoryEntryFromLineNumber(lineNumber);
            entry.IsSelected = !entry.IsSelected;
            OnSelectionChanged();
            return entry.Span.GetSpan(_historyTextBuffer.CurrentSnapshot);
        }

        public void SelectNextHistoryEntry() {
            if (!HasEntries) {
                return;
            }

            IRHistoryEntry entryToSelect;
            if (HasSelectedEntries) {
                var lastSelectedEntry = _entries.GetSelectedEntries().Last();
                if (lastSelectedEntry.Next == null) {
                    return;
                }

                ClearHistoryEntrySelection();
                entryToSelect = lastSelectedEntry.Next;
            } else {
                if (VisualComponent == null) {
                    return;
                }

                if (VisualComponent.TextView.Selection.IsEmpty) {
                    entryToSelect = _entries.First();
                } else {
                    var entry = GetHistoryEntryFromPosition(VisualComponent.TextView.Selection.End.Position);
                    if (entry.Next == null) {
                        return;
                    }

                    VisualComponent.TextView.Selection.Clear();
                    entryToSelect = entry.Next;
                }
            }

            SelectAndDispalyEntry(entryToSelect, ViewRelativePosition.Bottom);
        }

        public void SelectPreviousHistoryEntry() {
            if (!HasEntries) {
                return;
            }

            IRHistoryEntry entryToSelect;
            if (HasSelectedEntries) {
                var firstSelectedEntry = _entries.GetSelectedEntries().First();
                if (firstSelectedEntry.Previous == null) {
                    return;
                }

                ClearHistoryEntrySelection();
                entryToSelect = firstSelectedEntry.Previous;
            } else {
                if (VisualComponent == null) {
                    return;
                }

                if (VisualComponent.TextView.Selection.IsEmpty) {
                    entryToSelect = _entries.Last();
                } else {
                    var entry = GetHistoryEntryFromPosition(VisualComponent.TextView.Selection.Start.Position);
                    if (entry.Previous == null) {
                        return;
                    }

                    VisualComponent.TextView.Selection.Clear();
                    entryToSelect = entry.Previous;
                }
            }

            SelectAndDispalyEntry(entryToSelect, ViewRelativePosition.Top);
        }

        private void SelectAndDispalyEntry(IRHistoryEntry entryToSelect, ViewRelativePosition relativeTo) {
            entryToSelect.IsSelected = true;

            var snapshotPoint = relativeTo == ViewRelativePosition.Top 
                ? entryToSelect.Span.GetStartPoint(_historyTextBuffer.CurrentSnapshot)
                : entryToSelect.Span.GetEndPoint(_historyTextBuffer.CurrentSnapshot);

            var line = VisualComponent.TextView.GetTextViewLineContainingBufferPosition(snapshotPoint);
            if (line.VisibilityState != VisibilityState.FullyVisible) {
                VisualComponent.TextView.DisplayTextLineContainingBufferPosition(snapshotPoint, 0, relativeTo);
            }

            OnSelectionChanged();
        }

        public void SelectAllEntries() {
            if (!HasEntries) {
                return;
            }

            _entries.SelectAll();
            OnSelectionChanged();
        }

        public void ClearHistoryEntrySelection() {
            if (!HasSelectedEntries) {
                return;
            }

            _entries.UnselectAll();
            OnSelectionChanged();
        }

        public void DeleteSelectedHistoryEntries() {
            if (!HasSelectedEntries) {
                return;
            }

            DeleteSelectedTrackingSpans();
            _entries.RemoveSelected();
            OnSelectionChanged();
        }

        public void DeleteAllHistoryEntries() {
            var raiseEvent = _entries.HasSelectedEntries;

            DeleteAllEntries();

            if (raiseEvent) {
                OnSelectionChanged();
            }
        }

        public void AddToHistory(string text) {
            text = text.RemoveWhiteSpaceLines();
            if (string.IsNullOrEmpty(text)) {
                return;
            }

            var hasEntries = _entries.HasEntries;
            var snapshot = _historyTextBuffer.CurrentSnapshot;

            using (AdjustAddToHistoryScrolling())
            using (EditTextBuffer()) {
                if (hasEntries) {
                    snapshot = _historyTextBuffer.Insert(snapshot.Length, BlockSeparator);
                }

                var position = snapshot.Length;
                snapshot = _historyTextBuffer.Insert(position, text);
                _entries.Add(snapshot.CreateTrackingSpan(new Span(position, text.Length), SpanTrackingMode.EdgeExclusive));
            }
        }

        private IDisposable AdjustAddToHistoryScrolling() {
            if (VisualComponent == null) {
                return Disposable.Empty;
            }

            var firstVisibleLine = VisualComponent.TextView.TextViewLines.FirstOrDefault(l => l.VisibilityState == VisibilityState.FullyVisible);
            var lastLine = VisualComponent.TextView.TextViewLines.LastOrDefault();
            var moveScrollingToLastLine = firstVisibleLine == null || lastLine == null || lastLine.VisibilityState == VisibilityState.FullyVisible;
            if (moveScrollingToLastLine) {
                return Disposable.Create(() => {
                    var last = VisualComponent.TextView.TextViewLines.LastOrDefault();
                    if (last == null || last.VisibilityState == VisibilityState.FullyVisible) {
                        return;
                    }
                    var snapshotPoint = new SnapshotPoint(_historyTextBuffer.CurrentSnapshot, _historyTextBuffer.CurrentSnapshot.Length);
                    VisualComponent.TextView.DisplayTextLineContainingBufferPosition(snapshotPoint, 0, ViewRelativePosition.Bottom);
                });
            }

            var offset = firstVisibleLine.Top - VisualComponent.TextView.ViewportTop;
            return Disposable.Create(() => {
                VisualComponent.TextView.DisplayTextLineContainingBufferPosition(firstVisibleLine.Start, offset, ViewRelativePosition.Top);
            });
        }

        private void CreateEntries(string[] historyLines) {
            if (_historyTextBuffer == null) {
                return;
            }

            historyLines = historyLines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Replace(LineSeparator, BlockSeparator))
                .ToArray();

            using (EditTextBuffer()) {
                var text = string.Join(BlockSeparator, historyLines);
                _historyTextBuffer.Replace(new Span(0, _historyTextBuffer.CurrentSnapshot.Length), text);
            }

            var position = 0;
            var snapshot = _historyTextBuffer.CurrentSnapshot;
            foreach (var historyLine in historyLines) {
                var span = new Span(position, historyLine.Length);
                _entries.Add(snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeExclusive));
                position = snapshot.GetLineFromPosition(span.End).EndIncludingLineBreak;
            }
        }

        private void DeleteAllEntries() {
            using (EditTextBuffer()) {
                _entries.RemoveAll();
                _historyTextBuffer?.Delete(new Span(0, _historyTextBuffer.CurrentSnapshot.Length));
            }
        }

        private void DeleteSelectedTrackingSpans() {
            var selectedEntries = _entries.GetSelectedEntries();
            using (EditTextBuffer()) {
                foreach (var entry in selectedEntries) {
                    var snapshot = _historyTextBuffer.CurrentSnapshot;
                    SnapshotPoint startPoint, endPoint;
                    if (entry.Previous != null) {
                        startPoint = entry.Previous.Span.GetEndPoint(snapshot);
                        endPoint = entry.Span.GetEndPoint(snapshot);
                    } else {
                        startPoint = entry.Span.GetStartPoint(snapshot);
                        endPoint = entry.Next?.Span.GetStartPoint(snapshot) ?? entry.Span.GetEndPoint(snapshot);
                    }

                    var span = new SnapshotSpan(startPoint, endPoint);
                    _historyTextBuffer.Delete(span);
                }
            }
        }

        private IDisposable EditTextBuffer() {
            if (_readOnlyRegion != null) {
                using (var edit = _historyTextBuffer.CreateReadOnlyRegionEdit()) {
                    edit.RemoveReadOnlyRegion(_readOnlyRegion);
                    _readOnlyRegion = null;
                    edit.Apply();
                }
            }

            HistoryChanging?.Invoke(this, new EventArgs());
            return _textBufferIsEditable.Increment();
        }

        private void MakeTextBufferReadOnly() {
            using (var edit = _historyTextBuffer.CreateReadOnlyRegionEdit()) {
                var span = new Span(0, edit.Snapshot.Length);
                _readOnlyRegion = edit.CreateReadOnlyRegion(span, SpanTrackingMode.EdgeInclusive, EdgeInsertionMode.Deny);
                edit.Apply();
            }

            _currentEntry = null;
            HistoryChanged?.Invoke(this, new EventArgs());
        }

        private void OnSelectionChanged() {
            SelectionChanged?.Invoke(this, new EventArgs());
            _interactiveWorkflow.ActiveWindow?.Container.UpdateCommandStatus(false);
        }

        private IRHistoryEntry GetHistoryEntryFromPosition(int position) {
            var snapshot = _historyTextBuffer.CurrentSnapshot;
            var line = snapshot.GetLineFromPosition(position);
            return _entries.Find(b => b.Span != null && b.Span.GetSpan(snapshot).Contains(line.Extent));
        }

        private IRHistoryEntry GetHistoryEntryFromLineNumber(int lineNumber) {
            var snapshot = _historyTextBuffer.CurrentSnapshot;
            var line = snapshot.GetLineFromLineNumber(lineNumber);
            return _entries.Find(b => b.Span != null && b.Span.GetSpan(snapshot).Contains(line.Extent));
        }

        public void Dispose() {
            _dispose();
        }
    }
}