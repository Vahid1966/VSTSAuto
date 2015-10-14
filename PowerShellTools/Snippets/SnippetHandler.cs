﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.OptionsExtensionMethods;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using MSXML;

namespace PowerShellTools.Snippets
{
    [Export(typeof(IVsTextViewCreationListener))]
    [Name("snippets")]
    [ContentType("PowerShell")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    public class SnippetHandlerProvider : IVsTextViewCreationListener
    {
       // internal const string LanguageServiceGuidStr = "AD4D401C-11EA-431F-A412-FAB167156206";

        [Import]
        internal ITextStructureNavigatorSelectorService NavigatorService { get; set; }

        [Import]
        internal IVsEditorAdaptersFactoryService AdapterService = null;
        [Import]
        internal ICompletionBroker CompletionBroker { get; set; }
        [Import]
        internal SVsServiceProvider ServiceProvider { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            ITextView textView = AdapterService.GetWpfTextView(textViewAdapter);
            if (textView == null)
                return;

            textView.Properties.GetOrCreateSingletonProperty(() => new SnippetHandler(textViewAdapter, this, AdapterService, textView));
        }
    }

    internal class SnippetHandler : IOleCommandTarget, IVsExpansionClient
    {
        private readonly IVsTextLines _lines;
        private readonly IVsTextView _view;
        private readonly ITextView _textView;
        private readonly IVsEditorAdaptersFactoryService _adapterFactory;
        private IVsExpansionSession _session;
        private bool _sessionEnded, _selectEndSpan;
        private ITrackingPoint _selectionStart, _selectionEnd;

        public const string SurroundsWith = "SurroundsWith";
        public const string SurroundsWithStatement = "SurroundsWithStatement";
        public const string Expansion = "Expansion";

        IVsExpansionManager _mExManager;
        private readonly IOleCommandTarget _mNextCommandHandler;
        private readonly SnippetHandlerProvider _mProvider;

        private static string[] _allStandardSnippetTypes = { Expansion, SurroundsWith };
        private static string[] _surroundsWithSnippetTypes = { SurroundsWith, SurroundsWithStatement };

        [Import]
        internal IVsEditorAdaptersFactoryService AdapterFactory { get; set; }

        [Import]
        internal ITextStructureNavigatorSelectorService NavigatorService { get; set; }

        internal SnippetHandler(IVsTextView textViewAdapter, SnippetHandlerProvider provider, IVsEditorAdaptersFactoryService adaptersFactory, ITextView textView)
        {
            _adapterFactory = adaptersFactory;
            _textView = textView;
            _view = textViewAdapter;
            _mProvider = provider;
            //get the text manager from the service provider
            var textManager = (IVsTextManager2)_mProvider.ServiceProvider.GetService(typeof(SVsTextManager));
            textManager.GetExpansionManager(out _mExManager);
            _session = null;

            //add the command to the command chain
            textViewAdapter.AddCommandFilter(this, out _mNextCommandHandler);

            _lines = (IVsTextLines)_adapterFactory.GetBufferAdapter(_textView.TextBuffer);
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (VsShellUtilities.IsInAutomationFunction(_mProvider.ServiceProvider))
            {
                return _mNextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
            }

            if (pguidCmdGroup != VSConstants.VSStd2K)
                return _mNextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);

            switch ((VSConstants.VSStd2KCmdID) nCmdID)
            {
                case VSConstants.VSStd2KCmdID.RETURN:
                    if (InSession && ErrorHandler.Succeeded(EndCurrentExpansion(false)))
                    {
                        return VSConstants.S_OK;
                    }
                    break;
                case VSConstants.VSStd2KCmdID.TAB:
                    if (InSession && ErrorHandler.Succeeded(NextField()))
                    {
                        return VSConstants.S_OK;
                    }
                    break;
                case VSConstants.VSStd2KCmdID.BACKTAB:
                    if (InSession && ErrorHandler.Succeeded(PreviousField()))
                    {
                        return VSConstants.S_OK;
                    }
                    break;
                case VSConstants.VSStd2KCmdID.SURROUNDWITH:
                case VSConstants.VSStd2KCmdID.INSERTSNIPPET:
                    var textManager = (IVsTextManager2)_mProvider.ServiceProvider.GetService(typeof(SVsTextManager));
                    textManager.GetExpansionManager(out _mExManager);
                    TriggerSnippet(nCmdID);
                    return VSConstants.S_OK;
            }

            return _mNextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        private void TriggerSnippet(uint nCmdID)
        {
            if (_mExManager == null) return;

            string prompt;
            string[] snippetTypes;
            if ((VSConstants.VSStd2KCmdID)nCmdID == VSConstants.VSStd2KCmdID.SURROUNDWITH)
            {
                prompt = "Surround with:";
                snippetTypes = _surroundsWithSnippetTypes;
            }
            else
            {
                prompt = "Insert snippet:";
                snippetTypes = _allStandardSnippetTypes;
            }

            _mExManager.InvokeInsertionUI(
                _view,
                this,
                new Guid(GuidList.PowerShellLanguage),
                snippetTypes, 
                snippetTypes.Length,
                0,
                null,
                0,
                0,
                prompt,
                ">"
                );
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (VsShellUtilities.IsInAutomationFunction(_mProvider.ServiceProvider))
                return _mNextCommandHandler.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);

            if (pguidCmdGroup != VSConstants.VSStd2K || cCmds <= 0)
                return _mNextCommandHandler.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);

            if (prgCmds[0].cmdID != (uint) VSConstants.VSStd2KCmdID.INSERTSNIPPET)
                return _mNextCommandHandler.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);

            prgCmds[0].cmdf = (int)Constants.MSOCMDF_ENABLED | (int)Constants.MSOCMDF_SUPPORTED;
            return VSConstants.S_OK;
        }

        public bool InSession
        {
            get
            {
                return _session != null;
            }
        }

        public int EndExpansion()
        {
            _session = null;
            _sessionEnded = true;
            _selectionStart = _selectionEnd = null;
            return VSConstants.S_OK;
        }

        public int FormatSpan(IVsTextLines pBuffer, TextSpan[] ts)
        {
            IXMLDOMNode codeNode, snippetTypes, declarations, imports;

            int hr;
            if (ErrorHandler.Failed(hr = _session.GetSnippetNode("CodeSnippet:Code", out codeNode)))
            {
                return hr;
            }

            if (ErrorHandler.Failed(hr = _session.GetHeaderNode("CodeSnippet:SnippetTypes", out snippetTypes)))
            {
                return hr;
            }

            var declList = new List<string>();
            if (ErrorHandler.Succeeded(hr = _session.GetSnippetNode("CodeSnippet:Declarations", out declarations))
                && declarations != null)
            {
                foreach (IXMLDOMNode declType in declarations.childNodes)
                {
                    var id = declType.selectSingleNode("./CodeSnippet:ID");
                    if (id != null)
                    {
                        declList.Add(id.text);
                    }
                }
            }

            var importList = new List<string>();
            if (ErrorHandler.Succeeded(hr = _session.GetSnippetNode("CodeSnippet:Imports", out imports))
                && imports != null)
            {
                foreach (IXMLDOMNode import in imports.childNodes)
                {
                    var id = import.selectSingleNode("./CodeSnippet:Namespace");
                    if (id != null)
                    {
                        importList.Add(id.text);
                    }
                }
            }

            bool surroundsWith = false, surroundsWithStatement = false;
            foreach (IXMLDOMNode snippetType in snippetTypes.childNodes)
            {
                if (snippetType.nodeName == "SnippetType")
                {
                    if (snippetType.text == SurroundsWith)
                    {
                        surroundsWith = true;
                    }
                    else if (snippetType.text == SurroundsWithStatement)
                    {
                        surroundsWithStatement = true;
                    }
                }
            }

            // get the indentation of where we're inserting the code...
            string baseIndentation = GetBaseIndentation(ts);

            TextSpan? endSpan = null;
            using (var edit = _textView.TextBuffer.CreateEdit())
            {
                if (surroundsWith || surroundsWithStatement)
                {
                    // this is super annoyning...  Most languages can do a surround with and $selected$ can be
                    // an empty string and everything's the same.  But in Python we can't just have something like
                    // "while True: " without a pass statement.  So if we start off with an empty selection we
                    // need to insert a pass statement.  This is the purpose of the "SurroundsWithStatement"
                    // snippet type.
                    //
                    // But, to make things even more complicated, we don't have a good indication of what's the 
                    // template text vs. what's the selected text.  We do have access to the original template,
                    // but all of the values have been replaced with their default values when we get called
                    // here.  So we need to go back and re-apply the template, except for the $selected$ part.
                    //
                    // Also, the text has \n, but the inserted text has been replaced with the appropriate newline
                    // character for the buffer.
                    var templateText = codeNode.text.Replace("\n", _textView.Options.GetNewLineCharacter());
                    foreach (var decl in declList)
                    {
                        string defaultValue;
                        if (ErrorHandler.Succeeded(_session.GetFieldValue(decl, out defaultValue)))
                        {
                            templateText = templateText.Replace("$" + decl + "$", defaultValue);
                        }
                    }
                    templateText = templateText.Replace("$end$", "");

                    // we can finally figure out where the selected text began witin the original template...
                    int selectedIndex = templateText.IndexOf("$selected$");
                    if (selectedIndex != -1)
                    {
                        var selection = _textView.Selection;

                        // now we need to get the indentation of the $selected$ element within the template,
                        // as we'll need to indent the selected code to that level.
                        string indentation = GetTemplateSelectionIndentation(templateText, selectedIndex);

                        var start = _selectionStart.GetPosition(_textView.TextBuffer.CurrentSnapshot);
                        var end = _selectionEnd.GetPosition(_textView.TextBuffer.CurrentSnapshot);
                        if (end < start)
                        {
                            // we didn't actually have a selction, and our negative tracking pushed us
                            // back to the start of the buffer...
                            end = start;
                        }
                        var selectedSpan = Span.FromBounds(start, end);

                        if (surroundsWithStatement &&
                            String.IsNullOrWhiteSpace(_textView.TextBuffer.CurrentSnapshot.GetText(selectedSpan)))
                        {
                            // we require a statement here and the user hasn't selected any code to surround,
                            // so we insert a pass statement (and we'll select it after the completion is done)
                            int startPosition;
                            pBuffer.GetPositionOfLineIndex(ts[0].iStartLine, ts[0].iStartIndex, out startPosition);
                            edit.Replace(new Span(startPosition + selectedIndex, end - start), "pass");

                            // Surround With can be invoked with no selection, but on a line with some text.
                            // In that case we need to inject an extra new line.
                            var endLine = _textView.TextBuffer.CurrentSnapshot.GetLineFromPosition(end);
                            var endText = endLine.GetText().Substring(end - endLine.Start);
                            if (!String.IsNullOrWhiteSpace(endText))
                            {
                                edit.Insert(end, _textView.Options.GetNewLineCharacter());
                            }

                            // we want to leave the pass statement selected so the user can just
                            // continue typing over it...
                            var startLine = _textView.TextBuffer.CurrentSnapshot.GetLineFromPosition(startPosition + selectedIndex);
                            _selectEndSpan = true;
                            endSpan = new TextSpan()
                            {
                                iStartLine = startLine.LineNumber,
                                iEndLine = startLine.LineNumber,
                                iStartIndex = baseIndentation.Length + indentation.Length,
                                iEndIndex = baseIndentation.Length + indentation.Length + 4,
                            };
                        }

                        IndentSpan(
                            edit,
                            indentation,
                            _textView.TextBuffer.CurrentSnapshot.GetLineFromPosition(start).LineNumber + 1, // 1st line is already indented
                            _textView.TextBuffer.CurrentSnapshot.GetLineFromPosition(end).LineNumber
                        );
                    }
                }

                // we now need to update any code which was not selected  that we just inserted.
                IndentSpan(edit, baseIndentation, ts[0].iStartLine + 1, ts[0].iEndLine);

                edit.Apply();
            }

            if (endSpan != null)
            {
                _session.SetEndSpan(endSpan.Value);
            }

            return hr;
        }

        private static string GetTemplateSelectionIndentation(string templateText, int selectedIndex)
        {
            string indentation = "";
            for (int i = selectedIndex - 1; i >= 0; i--)
            {
                if (templateText[i] != '\t' && templateText[i] != ' ')
                {
                    indentation = templateText.Substring(i + 1, selectedIndex - i - 1);
                    break;
                }
            }
            return indentation;
        }

        private string GetBaseIndentation(TextSpan[] ts)
        {
            var indentationLine = _textView.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(ts[0].iStartLine).GetText();
            string baseIndentation = indentationLine;
            for (int i = 0; i < indentationLine.Length; i++)
            {
                if (indentationLine[i] != ' ' && indentationLine[i] != '\t')
                {
                    baseIndentation = indentationLine.Substring(0, i);
                    break;
                }
            }
            return baseIndentation;
        }

        private void IndentSpan(ITextEdit edit, string indentation, int startLine, int endLine)
        {
            var snapshot = _textView.TextBuffer.CurrentSnapshot;
            for (int i = startLine; i <= endLine; i++)
            {
                var curline = snapshot.GetLineFromLineNumber(i);
                edit.Insert(curline.Start, indentation);
            }
        }

        public int GetExpansionFunction(IXMLDOMNode xmlFunctionNode, string bstrFieldName, out IVsExpansionFunction pFunc)
        {
            pFunc = null;
            return VSConstants.S_OK;
        }

        public int IsValidKind(IVsTextLines pBuffer, TextSpan[] ts, string bstrKind, out int pfIsValidKind)
        {
            pfIsValidKind = 1;
            return VSConstants.S_OK;
        }

        public int IsValidType(IVsTextLines pBuffer, TextSpan[] ts, string[] rgTypes, int iCountTypes, out int pfIsValidType)
        {
            pfIsValidType = 1;
            return VSConstants.S_OK;
        }

        public int OnAfterInsertion(IVsExpansionSession pSession)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeInsertion(IVsExpansionSession pSession)
        {
            return VSConstants.S_OK;
        }

        public int PositionCaretForEditing(IVsTextLines pBuffer, TextSpan[] ts)
        {
            return VSConstants.S_OK;
        }

        public int OnItemChosen(string pszTitle, string pszPath)
        {
            int caretLine, caretColumn;
            GetCaretPosition(out caretLine, out caretColumn);

            var textSpan = new TextSpan() { iStartLine = caretLine, iStartIndex = caretColumn, iEndLine = caretLine, iEndIndex = caretColumn };
            return InsertNamedExpansion(pszTitle, pszPath, textSpan);
        }

        public int InsertNamedExpansion(string pszTitle, string pszPath, TextSpan textSpan)
        {
            if (_session != null)
            {
                // if the user starts an expansion session while one is in progress
                // then abort the current expansion session
                _session.EndCurrentExpansion(1);
                _session = null;
            }

            var expansion = _lines as IVsExpansion;
            if (expansion == null) return VSConstants.S_OK;

            var selection = _textView.Selection;
            var snapshot = selection.Start.Position.Snapshot;

            _selectionStart = snapshot.CreateTrackingPoint(selection.Start.Position, PointTrackingMode.Positive);
            _selectionEnd = snapshot.CreateTrackingPoint(selection.End.Position, PointTrackingMode.Negative);
            _selectEndSpan = _sessionEnded = false;

            var hr = expansion.InsertNamedExpansion(
                pszTitle,
                pszPath,
                textSpan,
                this,
                new Guid(GuidList.PowerShellLanguage),
                0,
                out _session
                );

            if (ErrorHandler.Succeeded(hr))
            {
                if (_sessionEnded)
                {
                    _session = null;
                }
            }
            return VSConstants.S_OK;
        }

        public int NextField()
        {
            return _session.GoToNextExpansionField(0);
        }

        public int PreviousField()
        {
            return _session.GoToPreviousExpansionField();
        }

        private void GetCaretPosition(out int caretLine, out int caretColumn)
        {
            ErrorHandler.ThrowOnFailure(_view.GetCaretPos(out caretLine, out caretColumn));

            // Handle virtual space
            int lineLength;
            ErrorHandler.ThrowOnFailure(_lines.GetLengthOfLine(caretLine, out lineLength));

            if (caretColumn > lineLength)
            {
                caretColumn = lineLength;
            }
        }

        public int EndCurrentExpansion(bool leaveCaret)
        {
            if (_selectEndSpan)
            {
                TextSpan[] endSpan = new TextSpan[1];
                if (ErrorHandler.Succeeded(_session.GetEndSpan(endSpan)))
                {
                    var snapshot = _textView.TextBuffer.CurrentSnapshot;
                    var startLine = snapshot.GetLineFromLineNumber(endSpan[0].iStartLine);
                    var span = new Span(startLine.Start + endSpan[0].iStartIndex, 4);
                    _textView.Caret.MoveTo(new SnapshotPoint(snapshot, span.Start));
                    _textView.Selection.Select(new SnapshotSpan(_textView.TextBuffer.CurrentSnapshot, span), false);
                    return _session.EndCurrentExpansion(1);
                }
            }
            return _session.EndCurrentExpansion(leaveCaret ? 1 : 0);
        }


        private bool InsertAnyExpansion(string shortcut, string title, string path)
        {
            //first get the location of the caret, and set up a TextSpan 
            int endColumn, startLine;
            //get the column number from  the IVsTextView, not the ITextView
            _view.GetCaretPos(out startLine, out endColumn);

            var addSpan = new TextSpan();
            addSpan.iStartIndex = endColumn;
            addSpan.iEndIndex = endColumn;
            addSpan.iStartLine = startLine;
            addSpan.iEndLine = startLine;

            if (shortcut != null) //get the expansion from the shortcut
            {
                //reset the TextSpan to the width of the shortcut,  
                //because we're going to replace the shortcut with the expansion
                addSpan.iStartIndex = addSpan.iEndIndex - shortcut.Length;

                _mExManager.GetExpansionByShortcut(
                    this,
                    new Guid(GuidList.PowerShellLanguage),
                    shortcut,
                    _view,
                    new[] { addSpan },
                    0,
                    out path,
                    out title);
            }

            if (title == null || path == null) return false;

            IVsTextLines textLines;
            _view.GetBuffer(out textLines);
            var bufferExpansion = (IVsExpansion)textLines;

            if (bufferExpansion == null) return false;

            var hr = bufferExpansion.InsertNamedExpansion(
                title,
                path,
                addSpan,
                this,
                new Guid(GuidList.PowerShellLanguage),
                0,
                out _session);

            return VSConstants.S_OK == hr;
        }
    }

    
}
