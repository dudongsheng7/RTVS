﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.Languages.Core.Text;
using Microsoft.Languages.Core.Tokens;
using Microsoft.R.Core.AST.Statements;
using Microsoft.R.Core.Parser;
using Microsoft.R.Core.Tokens;

namespace Microsoft.R.Core.AST.Scopes {
    /// <summary>
    /// Represents { } block. Scope may be standalone or be part
    /// of conditional or loop statement.
    /// </summary>
    [DebuggerDisplay("Scope, Children: {Children.Count} [{Start}...{End})")]
    public class Scope : AstNode, IScope {
        private TextRangeCollection<IStatement> statements = new TextRangeCollection<IStatement>();

        #region IScope
        /// <summary>
        /// Scope name
        /// </summary>
        public string Name { get; internal set; }

        public TokenNode OpenCurlyBrace { get; private set; }

        public TokenNode CloseCurlyBrace { get; private set; }
        #endregion

        #region ITextRange
        public override int End {
            get {
                if (CloseCurlyBrace == null && Root != null) {
                    return Root.TextProvider.Length;
                }
                return base.End;
            }
        }
        #endregion

        public Scope() :
            this("_Anonymous_") {
        }

        public Scope(string name) {
            this.Name = name;
        }

        public override bool Parse(ParseContext context, IAstNode parent) {
            TokenStream<RToken> tokens = context.Tokens;
            RToken currentToken = tokens.CurrentToken;

            context.Scopes.Push(this);

            if (!(this is GlobalScope) && currentToken.TokenType == RTokenType.OpenCurlyBrace) {
                this.OpenCurlyBrace = RParser.ParseToken(context, this);
            }

            while (!tokens.IsEndOfStream()) {
                currentToken = context.Tokens.CurrentToken;

                switch (currentToken.TokenType) {
                    case RTokenType.CloseCurlyBrace:
                        if (this.OpenCurlyBrace != null) {
                            this.CloseCurlyBrace = RParser.ParseToken(context, this);
                        } else {
                            context.AddError(new ParseError(ParseErrorType.UnexpectedToken, ErrorLocation.Token, currentToken));
                            context.Tokens.MoveToNextToken();
                        }
                        break;

                    case RTokenType.OpenCurlyBrace:
                        IScope scope = new Scope(string.Empty);
                        scope.Parse(context, this);
                        break;

                    default:
                        IStatement statement = Statement.Create(context, this, null);
                        if (statement != null) {
                            if (statement.Parse(context, this)) {
                                this.statements.Add(statement);
                            } else {
                                statement = null;
                            }
                        }

                        if (statement == null && context.Tokens.CurrentToken.TokenType != RTokenType.CloseCurlyBrace) {
                            if (!context.TextProvider.IsNewLineBeforePosition(context.Tokens.CurrentToken.Start)) {
                                // try recovering at the next line or past nearest 
                                // semicolon or closing curly brace
                                tokens.MoveToNextLine(context.TextProvider,
                                    (TokenStream<RToken> ts) => {
                                        return ts.CurrentToken.TokenType == RTokenType.Semicolon ||
                                               ts.NextToken.TokenType == RTokenType.CloseCurlyBrace;
                                    });
                            } else {
                                tokens.MoveToNextToken();
                            }
                        }
                        break;
                }

                if (this.CloseCurlyBrace != null) {
                    break;
                }
            }

            context.Scopes.Pop();

            if (this.OpenCurlyBrace != null && this.CloseCurlyBrace == null) {
                context.AddError(new MissingItemParseError(ParseErrorType.CloseCurlyBraceExpected, context.Tokens.PreviousToken));
            }

            // TODO: process content and fill out declared variables 
            // and functions and get data to the classifier for colorization.
            return base.Parse(context, parent);
        }

        public override string ToString() {
            return this.Name != null ? this.Name : string.Empty;
        }
    }
}
