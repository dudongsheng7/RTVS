﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions.Execution;
using FluentAssertions.Specialized;

namespace Microsoft.UnitTests.Core.FluentAssertions {
    internal sealed class AsyncAssertions {
        private readonly Func<Task> _asyncAction;

        public AsyncAssertions(Func<Task> asyncAction) {
            _asyncAction = asyncAction;
        }

        public async Task<ExceptionAssertions<TException>> ShouldThrowAsync<TException>(string because, object[] becauseArgs)
            where TException : Exception {
            var exceptions = await InvokeAction();

            Execute.Assertion
                .ForCondition(exceptions.Any())
                .BecauseOf(because, becauseArgs)
                .FailWith("Expected {0}{reason}, but no exception was thrown.", typeof(TException));

            var typedExceptions = exceptions.OfType<TException>().ToList();
            Execute.Assertion
                .ForCondition(typedExceptions.Any())
                .BecauseOf(because, becauseArgs)
                .FailWith("Expected {0}{reason}, but found {1}.", typeof(TException), exceptions);

            return new ExceptionAssertions<TException>(typedExceptions);
        }

        private Task<List<Exception>> InvokeAction() => _asyncAction().ContinueWith(t => {
            switch (t.Status) {
                case TaskStatus.Canceled:
                    return GetCanceledException(t);
                case TaskStatus.Faulted:
                    return new List<Exception>(t.Exception.Flatten().InnerExceptions);
                default:
                    return new List<Exception>();
            }
        });

        private static List<Exception> GetCanceledException(Task task) {
            var exceptions = new List<Exception>();
            try {
                task.GetAwaiter().GetResult();
            } catch (Exception ex) {
                exceptions.Add(ex);   
            }
            return exceptions;
        }
    }
}