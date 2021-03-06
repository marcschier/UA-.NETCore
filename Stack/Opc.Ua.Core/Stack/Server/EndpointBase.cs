/* Copyright (c) 1996-2016, OPC Foundation. All rights reserved.
   The source code in this file is covered under a dual-license scenario:
     - RCL: for OPC Foundation members in good-standing
     - GPL V2: everybody else
   RCL license terms accompanied with this source code. See http://opcfoundation.org/License/RCL/1.00/
   GNU General Public License as published by the Free Software Foundation;
   version 2 of the License are accompanied with this source code. See http://opcfoundation.org/License/GPLv2
   This source code is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Threading;
using System.Threading.Tasks;
using Opc.Ua.Bindings;

namespace Opc.Ua
{
    /// <summary>
	/// A base class for UA endpoints.
	/// </summary>
    public abstract class EndpointBase : IEndpointBase, ITransportListenerCallback
    {    
        #region Constructors
        /// <summary>
        /// Initializes the object when it is created by the WCF framework.
        /// </summary>
        protected EndpointBase()
        {
            SupportedServices  = new Dictionary<ExpandedNodeId,ServiceDefinition>();
            
            try
            {
                m_host = GetHostForContext();
                m_server = GetServerForContext();

                MessageContext = m_server.MessageContext;
               
                EndpointDescription = GetEndpointDescription();
            }
            catch (Exception e)
            {
                ServerError = new ServiceResult(e);
                EndpointDescription = null;

                m_host = null;
                m_server = null;
            }
        }

        /// <summary>
        /// Initializes the when it is created directly.
        /// </summary>
        /// <param name="host">The host.</param>
        protected EndpointBase(IServiceHostBase host)
        {
            if (host == null) throw new ArgumentNullException("host");

            m_host = host;
            m_server = host.Server;
            
            SupportedServices  = new Dictionary<ExpandedNodeId,ServiceDefinition>();
        }

        /// <summary>
        /// Initializes the endpoint with a server instead of a host.
        /// </summary>
        protected EndpointBase(ServerBase server)
        {
            if (server == null) throw new ArgumentNullException("server");

            m_host = null;
            m_server = server;

            SupportedServices = new Dictionary<ExpandedNodeId, ServiceDefinition>();
        }
        #endregion
             
        #region ITransportListenerCallback Members
        /// <summary>
        /// Begins processing a request received via a binary encoded channel.
        /// </summary>
        /// <param name="channeId">A unique identifier for the secure channel which is the source of the request.</param>
        /// <param name="endpointDescription">The description of the endpoint which the secure channel is using.</param>
        /// <param name="request">The incoming request.</param>
        /// <returns>
        /// The response to return over the transport channel.
        /// </returns>
        public async Task<IServiceResponse> ProcessRequestAsync(
            string channeId, 
            EndpointDescription endpointDescription,
            IServiceRequest request)
        {
            request.ChannelContext = new SecureChannelContext(
                channeId,
                endpointDescription,
                RequestEncoding.Binary);

            return await ProcessRequestAsync(request).ConfigureAwait(false);
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Dispatches an incoming binary encoded request asynchronously to its handler bypassing any call to
        /// the <see cref="IServerBase.ScheduleIncomingRequest(IEndpointIncomingRequest)" /> processor since the
        /// request already came in on the Task based async path and does not need to be scheduled.  
        /// </summary>
        /// <param name="context">SecureChannelContext. Describes the channel context to use in processing</param>
        /// <param name="request">Incoming request to process.</param>
        /// <returns>
        /// The response to return over the channel.
        /// </returns>
        public virtual async Task<IServiceResponse> ProcessRequestAsync(IServiceRequest request)
        {
            try
            {
                SetRequestContext(RequestEncoding.Binary);

                ServiceDefinition service = null;

                // find service.
                if (!SupportedServices.TryGetValue(request.TypeId, out service))
                {
                    throw new ServiceResultException(StatusCodes.BadServiceUnsupported, 
                        Utils.Format("'{0}' is an unrecognized service identifier.", request.TypeId));
                }

                // invoke service.
                return await service.InvokeAsync(request).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // create fault.
                return CreateFault(request, e);
            }
        }
        #endregion

        #region IEndpointBase Members
        /// <summary>
        /// Dispatches an incoming binary encoded request.
        /// </summary>
        /// <param name="request">Request.</param>
        /// <returns>Invoke service response message.</returns>
        public virtual async Task<InvokeServiceResponseMessage> InvokeServiceAsync(InvokeServiceMessage request)
        {
            IServiceRequest decodedRequest = null;
            IServiceResponse response = null;

            // create context for request and reply.
            ServiceMessageContext context = MessageContext;

            try
            {
                // check for null.
                if (request == null || request.InvokeServiceRequest == null)
                {
                    throw new ServiceResultException(StatusCodes.BadDecodingError, Utils.Format("Null message cannot be processed."));
                }

                // decoding incoming message.
                decodedRequest = BinaryDecoder.DecodeMessage(request.InvokeServiceRequest, null, context) as IServiceRequest;
                decodedRequest.ChannelContext = SecureChannelContext.Current;

                // invoke service.
                response = await ProcessRequestAsync(decodedRequest).ConfigureAwait(false);

                // encode response.
                return new InvokeServiceResponseMessage(BinaryEncoder.EncodeMessage(response, context), request.ChannelContext);
            }
            catch (Exception e)
            {
                // create fault.
                ServiceFault fault = CreateFault(decodedRequest, e);

                // encode fault response.
                if (context == null)
                {
                    context = new ServiceMessageContext();
                }

                return new InvokeServiceResponseMessage(BinaryEncoder.EncodeMessage(fault, context), request.ChannelContext);
            }
        }

        /// <summary>
        /// Dispatches an incoming binary encoded request.
        /// </summary>
        public virtual IAsyncResult BeginInvokeService(InvokeServiceMessage message, AsyncCallback callack, object callbackData)
        {
            return TaskToApm.Begin(InvokeServiceAsync(message), callack, callbackData);
        }

        /// <summary>
        /// Dispatches an incoming binary encoded request.
        /// </summary>
        /// <param name="ar">The ar.</param>
        /// <returns></returns>
        public virtual InvokeServiceResponseMessage EndInvokeService(IAsyncResult ar)
        {
            return TaskToApm.End<InvokeServiceResponseMessage>(ar);
        }
        #endregion

        /// <summary>
        /// Returns the host associated with the current context.
        /// </summary>
        /// <value>The host associated with the current context.</value>
        protected IServiceHostBase HostForContext
        {
            get 
            { 
                if (m_host == null)
                {
                    m_host = GetHostForContext();
                }

                return m_host; 
            }
        }

        /// <summary>
        /// Returns the host associated with the current context.
        /// </summary>
        /// <returns>The host associated with the current context.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        protected static IServiceHostBase GetHostForContext()
        {
            // fetch the current operation context.
            OperationContext context = OperationContext.Current;

            if (context == null)
            {
                throw new ServiceResultException(StatusCodes.BadInternalError, "The current thread does not have a valid WCF operation context.");
            }

            throw new ServiceResultException(StatusCodes.BadInternalError, "The endpoint is not associated with a host that supports IServerHostBase.");
        }

        /// <summary>
        /// Gets the server object from the operation context.
        /// </summary>
        /// <value>The server object from the operation context.</value>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1721:PropertyNamesShouldNotMatchGetMethods")]
        protected IServerBase ServerForContext
        {
            get 
            { 
                if (m_server == null)
                {
                    m_server = GetServerForContext();
                }

                return m_server; 
            }
        }

        /// <summary>
        /// Gets the server object from the operation context.
        /// </summary>
        /// <returns>The server object from the operation context.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        protected IServerBase GetServerForContext()
        {
            // get the server associated with the host.
            IServerBase server = HostForContext.Server;

            if (server == null)
            {
                throw new ServiceResultException(StatusCodes.BadInternalError, "The endpoint is not associated with a server instance.");
            }

            // check the server status.
            if (ServiceResult.IsBad(server.ServerError))
            {
                throw new ServiceResultException(server.ServerError);
            }

            return server;
        }

        #region Protected Methods
        /// <summary>
        /// Find the endpoint description for the endpoint.
        /// </summary>
        protected EndpointDescription GetEndpointDescription()
        {
            return null;
        }
        
        /// <summary>
        /// Finds the service identified by the request type.
        /// </summary>
        protected ServiceDefinition FindService(ExpandedNodeId requestTypeId)
        {
            ServiceDefinition service = null;

            if (!SupportedServices.TryGetValue(requestTypeId, out service))
            {
                throw ServiceResultException.Create(
                    StatusCodes.BadServiceUnsupported, 
                    "'{0}' is an unrecognized service identifier.",
                    requestTypeId);
            }

            return service;
        }

        /// <summary>
        /// Creates a fault message.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="exception">The exception.</param>
        /// <returns>A fault message.</returns>
        protected static ServiceFault CreateFault(IServiceRequest request, Exception exception)
        {
            DiagnosticsMasks diagnosticsMask = DiagnosticsMasks.ServiceNoInnerStatus;

            ServiceFault fault = new ServiceFault();

            if (request != null)
            {
                fault.ResponseHeader.Timestamp     = DateTime.UtcNow;
                fault.ResponseHeader.RequestHandle = request.RequestHeader.RequestHandle;

                if (request.RequestHeader != null)
                {
                    diagnosticsMask = (DiagnosticsMasks)request.RequestHeader.ReturnDiagnostics;
                }
            }

            ServiceResult result = null;

            ServiceResultException sre = exception as ServiceResultException;

            if (sre != null)
            {
                result = new ServiceResult(sre);

                Utils.Trace(
                    Utils.TraceMasks.Service, 
                    "Service Fault Occured. Reason={0}", 
                    result);
            }
            else
            {
                result = new ServiceResult(exception, StatusCodes.BadUnexpectedError);
                Utils.Trace(exception, "SERVER - Unexpected Service Fault: {0}", exception.Message);
            }                               

            fault.ResponseHeader.ServiceResult = result.Code;

            StringTable stringTable = new StringTable();

            fault.ResponseHeader.ServiceDiagnostics = new DiagnosticInfo(
                result, 
                diagnosticsMask, 
                true, 
                stringTable);

            fault.ResponseHeader.StringTable = stringTable.ToArray();
 
            return fault;
        }

        /// <summary>
        /// Creates a fault message.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="exception">The exception.</param>
        /// <returns>A fault message.</returns>
        protected static Exception CreateSoapFault(IServiceRequest request, Exception exception)
        {
            ServiceFault fault = CreateFault(request, exception);

            // get the error from the header.
            ServiceResult error = fault.ResponseHeader.ServiceResult;

            if (error == null)
            {
                error = ServiceResult.Create(StatusCodes.BadUnexpectedError, "An unknown error occurred.");
            }
            
            // construct the fault code and fault reason.
            string codeName = StatusCodes.GetBrowseName(error.Code);

            FaultCode code = null;
            FaultReason reason = null;

            if (!LocalizedText.IsNullOrEmpty(error.LocalizedText))
            {
                reason = new FaultReason(new FaultReasonText(Utils.Format("{0}", error.LocalizedText)));
            }
            else
            {
                reason = new FaultReason(new FaultReasonText(codeName));
            }

            if (!String.IsNullOrEmpty(error.SymbolicId))
            {
                FaultCode subcode = new FaultCode(error.SymbolicId, error.NamespaceUri);
                code = new FaultCode(codeName, Namespaces.OpcUa, subcode);
            }
            else
            {
                code = new FaultCode(codeName, Namespaces.OpcUa);
            }

            // throw the fault.
            return new FaultException<ServiceFault>(fault, reason, code, string.Empty);
        }

        /// <summary>
        /// Returns the message context used by the server associated with the endpoint.
        /// </summary>
        /// <value>The message context.</value>
        protected ServiceMessageContext MessageContext
        {
            get { return m_messageContext;  }
            set { m_messageContext = value; }
        }

        /// <summary>
        /// Returns the description for the endpoint
        /// </summary>
        /// <value>The endpoint description.</value>
        protected EndpointDescription EndpointDescription
        {
            get { return m_endpointDescription;  }
            set { m_endpointDescription = value; }
        }

        /// <summary>
        /// The types known to the server.
        /// </summary>
        /// <value>The server error.</value>
        protected ServiceResult ServerError
        {
            get { return m_serverError;  }
            set { m_serverError = value; }
        }

        /// <summary>
        /// The types known to the server.
        /// </summary>
        protected Dictionary<ExpandedNodeId, ServiceDefinition> SupportedServices
        {
            get { return m_supportedServices; }
            set { m_supportedServices = value; }
        }
             
        /// <summary>
        /// Sets the request context for the thread.
        /// </summary>
        /// <param name="encoding">The encoding.</param>
        protected void SetRequestContext(RequestEncoding encoding)
        {
        }

        /// <summary>
        /// Called when a new request is received by the endpoint.
        /// </summary>
        /// <param name="request">The request.</param>
        protected virtual void OnRequestReceived(IServiceRequest request)
        {
        }

        /// <summary>
        /// Called when a response sent via the endpoint.
        /// </summary>
        /// <param name="response">The response.</param>
        protected virtual void OnResponseSent(IServiceResponse response)
        {
        }

        /// <summary>
        /// Called when a response fault sent via the endpoint.
        /// </summary>
        /// <param name="fault">The fault.</param>
        protected virtual void OnResponseFaultSent(Exception fault)
        {
        }

        #endregion

        #region ServiceDefinition Classe
        /// <summary>
        /// Stores the definition of a service supported by the server.
        /// </summary>
        protected class ServiceDefinition
        {
            /// <summary>
            /// Initializes the object with its request type and implementation.
            /// </summary>
            /// <param name="requestType">Type of the request.</param>
            /// <param name="invokeMethod">The invoke method.</param>
            public ServiceDefinition(
                Type requestType, 
                InvokeServiceEventHandler invokeMethod)
            {
                m_requestType = requestType;
                m_InvokeService = invokeMethod;
            }

            /// <summary>
            /// The system type of the request object.
            /// </summary>
            /// <value>The type of the request.</value>
            public Type RequestType
            {
                get { return m_requestType; }
            }

            /// <summary>
            /// The system type of the request object.
            /// </summary>
            /// <value>The type of the response.</value>
            public Type ResponseType
            {
                get { return m_requestType; }
            }            
            
            /// <summary>
            /// Processes the request.
            /// </summary>
            /// <param name="request">The request.</param>
            /// <returns></returns>
            public async Task<IServiceResponse> InvokeAsync(IServiceRequest request)
            {
                if (m_InvokeService != null)
                {
                    return await m_InvokeService(request).ConfigureAwait(false);
                }

                return null;
            }

            #region Private Fields
            private Type m_requestType;
            private InvokeServiceEventHandler m_InvokeService;
            #endregion
        }

        /// <summary>
        /// A delegate used to dispatch incoming service requests.
        /// </summary>
        protected delegate Task<IServiceResponse> InvokeServiceEventHandler(IServiceRequest request);
        #endregion

        #region ProcessRequestAsyncResult Class
        /// <summary>
        /// An AsyncResult object when handling an asynchronous request for self-scheduled servers.
        /// </summary>
        protected class ProcessRequestAsyncResult : AsyncResultBase, IEndpointIncomingRequest
        {
            #region Constructors
            /// <summary>
            /// Initializes a new instance of the <see cref="ProcessRequestAsyncResult"/> class.
            /// </summary>
            /// <param name="endpoint">The endpoint being called.</param>
            /// <param name="callback">The callback to use when the operation completes.</param>
            /// <param name="callbackData">The callback data.</param>
            /// <param name="timeout">The timeout in milliseconds</param>
            public ProcessRequestAsyncResult(
                EndpointBase endpoint,
                AsyncCallback callback,
                object callbackData,
                int timeout)
            :
                base(callback, callbackData, timeout)
            {
                m_endpoint = endpoint;
            }
            #endregion

            #region IEndpointIncomingRequest Members
            /// <summary>
            /// Gets the request.
            /// </summary>
            /// <value>The request.</value>
            public IServiceRequest Request
            {
                get { return m_request; }
            }

            /// <summary>
            /// Gets the secure channel context associated with the request.
            /// </summary>
            /// <value>The secure channel context.</value>
            public SecureChannelContext SecureChannelContext
            {
                get { return m_context; }
            }

            /// <summary>
            /// Gets or sets the call data associated with the request.
            /// </summary>
            /// <value>The call data.</value>
            public object Calldata
            {
                get { return m_calldata; }
                set { m_calldata = value; }
            }

            /// <summary>
            /// Used by legacy server implementations to signal request was handled and completed.  
            /// The default ServerBase implementation allows passes us null for response and 
            /// a good result, which triggers us to complete the request asynchrously for it
            /// through the asynchronous service delegate.
            /// </summary>
            /// <param name="response">The response generated by the server</param>
            /// <param name="error">any error</param>
            /// <returns>A task that promises completion</returns>
            public async Task OperationCompleted(IServiceResponse response, ServiceResult error)
            {
                if (ServiceResult.IsBad(error))
                {
                    m_error = new ServiceResultException(error);
                    m_response = SaveExceptionAsResponse(m_error);
                }

                else if (response == null)
                {
                    // Complete the request internally by invoking the async service
                    try
                    {
                        // set the context.
                        m_request.ChannelContext = m_context;

                        // call the service.
                        m_response = await m_service.InvokeAsync(m_request).ConfigureAwait(false);
                        m_error = null;
                    }
                    catch (Exception e)
                    {
                        // save any error.
                        m_error = e;
                        m_response = SaveExceptionAsResponse(e);
                    }
                }
                else
                {
                    m_response = response;
                    m_error = null;
                }

                // report completion.
                OperationCompleted();
            }
            #endregion

            #region Public Members
            /// <summary>
            /// Begins processing an incoming request.
            /// </summary>
            /// <param name="context">The security context for the request</param>
            /// <param name="request">The request.</param>
            /// <returns>The result object that is used to call the EndProcessRequest method.</returns>
            public IAsyncResult BeginProcessRequest(
                SecureChannelContext context,
                IServiceRequest request)
            {
                m_request = request;
                m_context = context;

                try
                {
                    // find service.
                    m_service = m_endpoint.FindService(m_request.TypeId);

                    if (m_service == null)
                    {
                        throw ServiceResultException.Create(StatusCodes.BadServiceUnsupported, "'{0}' is an unrecognized service type.", m_request.TypeId);
                    }

                    // queue request.
                    m_endpoint.ServerForContext.ScheduleIncomingRequest(this);
                }
                catch (Exception e)
                {
                    m_error = e;
                    m_response = SaveExceptionAsResponse(e);

                    // operation completed.
                    OperationCompleted();
                }

                return this;
            }

            /// <summary>
            /// Forward to base
            /// </summary>
            /// <param name="context"></param>
            /// <param name="request"></param>
            /// <returns></returns>
            public virtual Task<IServiceResponse> ProcessRequestAsync(IServiceRequest request)
            {
                return m_endpoint.ProcessRequestAsync(request);
            }

            /// <summary>
            /// Checks for a valid IAsyncResult object and waits for the operation to complete.
            /// </summary>
            /// <param name="ar">The IAsyncResult object for the operation.</param>
            /// <param name="throwOnError">if set to <c>true</c> an exception is thrown if an error occurred.</param>
            /// <returns>The response.</returns>
            public static IServiceResponse WaitForComplete(IAsyncResult ar, bool throwOnError)
            {
                ProcessRequestAsyncResult result = ar as ProcessRequestAsyncResult;

                if (result == null)
                {
                    throw new ArgumentException("End called with an invalid IAsyncResult object.", "ar");
                }

                if (result.m_response == null)
                {
                    if (!result.WaitForComplete())
                    {
                        throw new TimeoutException();
                    }
                }

                if (throwOnError && result.m_error != null)
                {
                    throw new ServiceResultException(result.m_error, StatusCodes.BadInternalError);
                }

                return result.m_response;
            }

            /// <summary>
            /// Checks for a valid IAsyncResult object and returns the original request object.
            /// </summary>
            /// <param name="ar">The IAsyncResult object for the operation.</param>
            /// <returns>The request object if available; otherwise null.</returns>
            public static IServiceRequest GetRequest(IAsyncResult ar)
            {
                ProcessRequestAsyncResult result = ar as ProcessRequestAsyncResult;

                if (result != null)
                {
                    return result.m_request;
                }

                return null;
            }
            #endregion

            #region Private Members
            /// <summary>
            /// Saves an exception as response.
            /// </summary>
            /// <param name="e">The exception.</param>
            private IServiceResponse SaveExceptionAsResponse(Exception e)
            {
                try
                {
                    return CreateFault(m_request, e);
                }
                catch (Exception e2)
                {
                    return CreateFault(null, e2);
                }
            }
            #endregion

            #region Private Fields
            private EndpointBase m_endpoint;
            private SecureChannelContext m_context;
            private IServiceRequest m_request;
            private IServiceResponse m_response;
            private ServiceDefinition m_service;
            private Exception m_error;
            private object m_calldata;
            #endregion
        }
        #endregion

        #region Private Fields
        private ServiceResult m_serverError;
        private ServiceMessageContext m_messageContext;
        private EndpointDescription m_endpointDescription;
        private Dictionary<ExpandedNodeId,ServiceDefinition> m_supportedServices;
        private IServiceHostBase m_host;
        private IServerBase m_server;
        private const string g_ImplementationString = "Opc.Ua.EndpointBase WCF Service " + AssemblyVersionInfo.CurrentVersion;
        #endregion
    }
}
