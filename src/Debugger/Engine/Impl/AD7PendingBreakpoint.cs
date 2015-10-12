﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;

namespace Microsoft.R.Debugger.Engine {
    internal sealed class AD7PendingBreakpoint : IDebugPendingBreakpoint2 {
        private readonly IDebugBreakpointRequest2 _request;
        private BP_REQUEST_INFO _requestInfo;
        private enum_PENDING_BP_STATE _state;
        private AD7BoundBreakpoint _boundBreakpoint;

        public AD7Engine Engine { get; }

        private bool IsDeleted => _state == enum_PENDING_BP_STATE.PBPS_DELETED;

        private bool IsEnabled => _state == enum_PENDING_BP_STATE.PBPS_ENABLED;

        public AD7PendingBreakpoint(AD7Engine engine, IDebugBreakpointRequest2 request) {
            Engine = engine;
            _request = request;

            var requestInfo = new BP_REQUEST_INFO[1];
            Marshal.ThrowExceptionForHR(request.GetRequestInfo(enum_BPREQI_FIELDS.BPREQI_BPLOCATION | enum_BPREQI_FIELDS.BPREQI_CONDITION | enum_BPREQI_FIELDS.BPREQI_ALLFIELDS, requestInfo));
            _requestInfo = requestInfo[0];

            if (_requestInfo.guidLanguage != DebuggerGuids.LanguageGuid) {
                throw new ArgumentException("Breakpoint request is not for R language", "request");
            }
        }

        private bool CanBind() {
            return !IsDeleted && _requestInfo.bpLocation.bpLocationType == (uint)enum_BP_LOCATION_TYPE.BPLT_CODE_FILE_LINE;
        }

        public void GetLocation(out string fileName, out int lineNumber, out TEXT_POSITION start, out TEXT_POSITION end) {
            var docPosition = (IDebugDocumentPosition2)(Marshal.GetObjectForIUnknown(_requestInfo.bpLocation.unionmember2));

            Marshal.ThrowExceptionForHR(docPosition.GetFileName(out fileName));

            var pStart = new TEXT_POSITION[1];
            var pEnd = new TEXT_POSITION[1];
            Marshal.ThrowExceptionForHR(docPosition.GetRange(pStart, pEnd));
            start = pStart[0];
            end = pEnd[0];

            lineNumber = (int)start.dwLine + 1;
        }

        int IDebugPendingBreakpoint2.Bind() {
            if (!CanBind()) {
                return VSConstants.S_FALSE;
            }

            string fileName;
            int lineNumber;
            TEXT_POSITION start, end;
            GetLocation(out fileName, out lineNumber, out start, out end);

            Engine.DebugSession.AddBreakpointAsync(fileName, lineNumber).GetAwaiter().GetResult();
            _boundBreakpoint = new AD7BoundBreakpoint(this, _state);

            return VSConstants.S_OK;
        }

        int IDebugPendingBreakpoint2.CanBind(out IEnumDebugErrorBreakpoints2 ppErrorEnum) {
            ppErrorEnum = null;
            return CanBind() ? VSConstants.S_OK : VSConstants.S_FALSE;
        }

        int IDebugPendingBreakpoint2.Delete() {
            _boundBreakpoint = null;
            _state = enum_PENDING_BP_STATE.PBPS_DELETED;
            return VSConstants.S_OK;
        }

        int IDebugPendingBreakpoint2.Enable(int fEnable) {
            if (_state == enum_PENDING_BP_STATE.PBPS_DELETED) {
                Debug.Fail($"Trying to enable or disable a deleted {nameof(AD7PendingBreakpoint)}");
                return VSConstants.E_FAIL;
            }

            _state = fEnable == 0 ? enum_PENDING_BP_STATE.PBPS_DISABLED : enum_PENDING_BP_STATE.PBPS_ENABLED;
            return VSConstants.S_OK;
        }

        int IDebugPendingBreakpoint2.EnumBoundBreakpoints(out IEnumDebugBoundBreakpoints2 ppEnum) {
            ppEnum = new AD7BoundBreakpointEnum(
                _boundBreakpoint == null ?
                new IDebugBoundBreakpoint2[] { } :
                new IDebugBoundBreakpoint2[] { _boundBreakpoint });
            return VSConstants.S_OK;
        }

        int IDebugPendingBreakpoint2.EnumErrorBreakpoints(enum_BP_ERROR_TYPE bpErrorType, out IEnumDebugErrorBreakpoints2 ppEnum) {
            ppEnum = null;
            return VSConstants.E_NOTIMPL;
        }

        int IDebugPendingBreakpoint2.GetBreakpointRequest(out IDebugBreakpointRequest2 ppBPRequest) {
            ppBPRequest = _request;
            return VSConstants.S_OK;
        }

        int IDebugPendingBreakpoint2.GetState(PENDING_BP_STATE_INFO[] pState) {
            pState[0] = default(PENDING_BP_STATE_INFO);
            pState[0].state = _state;
            return VSConstants.S_OK;
        }

        int IDebugPendingBreakpoint2.SetCondition(BP_CONDITION bpCondition) {
            _requestInfo.bpCondition = bpCondition;
            return VSConstants.S_OK;
        }

        int IDebugPendingBreakpoint2.SetPassCount(BP_PASSCOUNT bpPassCount) {
            _requestInfo.bpPassCount = bpPassCount;
            return VSConstants.S_OK;
        }

        int IDebugPendingBreakpoint2.Virtualize(int fVirtualize) {
            return VSConstants.S_OK;
        }
    }
}