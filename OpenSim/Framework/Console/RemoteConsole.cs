/*
 * Copyright (c) InWorldz Halcyon Developers
 * Copyright (c) Contributors, http://opensimulator.org/
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Xml;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using OpenMetaverse;
using Nini.Config;
using InWorldz.JWT;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using log4net;

namespace OpenSim.Framework.Console
{
    public class ConsoleConnection
    {
        public int last;
        public long lastLineSeen;
        public bool newConnection = true;
        public AsyncHttpRequest request = null;
    }

    // A console that uses REST interfaces
    //
    public class RemoteConsole : CommandConsole
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private IHttpServer m_Server;
        private IConfigSource m_Config;
        private List<string> m_Scrollback;
        private ManualResetEvent m_DataEvent;
        private List<string> m_InputData;
        private long m_LineNumber;
        private Dictionary<UUID, ConsoleConnection> m_Connections;
        private string m_AllowedOrigin;
        private JWTSignatureUtil m_sigUtil;

        public RemoteConsole(string defaultPrompt) : base(defaultPrompt)
        {
            m_Server = null;
            m_Config = null;
            m_Scrollback = new List<string>();
            m_DataEvent = new ManualResetEvent(false);
            m_InputData = new List<string>();
            m_LineNumber = 0;
            m_Connections = new Dictionary<UUID, ConsoleConnection>();
            m_AllowedOrigin = String.Empty;
            m_sigUtil = new JWTSignatureUtil(publicKeyPath: "./server.crt");
        }

        public void ReadConfig(IConfigSource config)
        {
            m_Config = config;

            IConfig netConfig = m_Config.Configs["Network"];
            if (netConfig == null)
                return;

            m_AllowedOrigin = netConfig.GetString("ConsoleAllowedOrigin", String.Empty);
        }

        public void SetServer(IHttpServer server)
        {
            m_Server = server;

            m_Server.AddHTTPHandler("/StartSession/", HandleHttpStartSession);
            m_Server.AddHTTPHandler("/CloseSession/", HandleHttpCloseSession);
            m_Server.AddHTTPHandler("/SessionCommand/", HandleHttpSessionCommand);
        }

        internal void ResponseReady()
        {
            List<AsyncHttpRequest> requests = new List<AsyncHttpRequest>();

            lock (m_Connections)
            {
                foreach (KeyValuePair<UUID, ConsoleConnection> kvp in m_Connections)
                {
                    ConsoleConnection connection = kvp.Value;
                    if (connection.request != null)
                    {
                        requests.Add(connection.request);
                        connection.request = null;
                    }
                }
            }

            foreach (AsyncHttpRequest request in requests)
            {
                request.SendResponse(ProcessEvents(request));
            }
        }

        public override void Output(string text, string level)
        {
            lock (m_Scrollback)
            {
                while (m_Scrollback.Count >= 1000)
                    m_Scrollback.RemoveAt(0);
                m_LineNumber++;
                m_Scrollback.Add(String.Format("{0}", m_LineNumber) + ":" + level + ":" + text);
            }

            FireOnOutput(text.Trim());
            System.Console.WriteLine(text.Trim());
            ResponseReady();
        }

        public override void Output(string text)
        {
            Output(text, "normal");
        }

        public override string ReadLine(string p, bool isCommand, bool e)
        {
            if (isCommand)
                Output("+++" + p);
            else
                Output("-++" + p);

            m_DataEvent.WaitOne();

            string cmdinput;

            lock (m_InputData)
            {
                if (m_InputData.Count == 0)
                {
                    m_DataEvent.Reset();
                    return String.Empty;
                }

                cmdinput = m_InputData[0];
                m_InputData.RemoveAt(0);
                if (m_InputData.Count == 0)
                    m_DataEvent.Reset();

            }

            if (isCommand)
            {
                string[] cmd = Commands.Resolve(Parser.Parse(cmdinput));

                if (cmd.Length != 0)
                {
                    int i;

                    for (i = 0; i < cmd.Length; i++)
                    {
                        if (cmd[i].Contains(" "))
                            cmd[i] = "\"" + cmd[i] + "\"";
                    }
                    return String.Empty;
                }
            }
            return cmdinput;
        }

        private Hashtable CheckOrigin(Hashtable result)
        {
            if (!string.IsNullOrEmpty(m_AllowedOrigin))
                result["access_control_allow_origin"] = m_AllowedOrigin;

            return result;
        }

        /* TODO: Figure out how PollServiceHTTPHandler can access the request headers
         * in order to use m_AllowedOrigin as a regular expression
        private Hashtable CheckOrigin(Hashtable headers, Hashtable result)
        {
            if (!string.IsNullOrEmpty(m_AllowedOrigin))
            {
                if (headers.ContainsKey("origin"))
                {
                    string origin = headers["origin"].ToString();
                    if (Regex.IsMatch(origin, m_AllowedOrigin))
                        result["access_control_allow_origin"] = origin;
                }
            }
            return result;
        }
        */

        private void DoExpire()
        {
            List<UUID> expired = new List<UUID>();
            List<AsyncHttpRequest> requests = new List<AsyncHttpRequest>();

            lock (m_Connections)
            {
                foreach (KeyValuePair<UUID, ConsoleConnection> kvp in m_Connections)
                {
                    if (System.Environment.TickCount - kvp.Value.last > 500000)
                        expired.Add(kvp.Key);
                }

                foreach (UUID id in expired)
                {
                    ConsoleConnection connection = m_Connections[id];
                    m_Connections.Remove(id);
                    CloseConnection(id);
                    if (connection.request != null)
                    {
                        requests.Add(connection.request);
                        connection.request = null;
                    }
                }
            }

            foreach (AsyncHttpRequest request in requests)
            {
                request.SendResponse(ProcessEvents(request));
            }
        }

        private Hashtable HandleHttpStartSession(Hashtable request)
        {
            DoExpire();

            Hashtable post = DecodePostString(request["body"].ToString());
            Hashtable reply = new Hashtable();

            reply["str_response_string"] = String.Empty;
            reply["int_response_code"] = 401;
            reply["content_type"] = "text/plain";

            var headers = (Hashtable)request["headers"];
            if (headers.ContainsKey("Authorization"))
            {
                var authHeader = headers["Authorization"].ToString();
                if (!authHeader.StartsWith("Bearer ", StringComparison.InvariantCultureIgnoreCase))
                {
                    m_log.Warn($"[REMOTECONSOLE] StartSession JWT Authorization header format failure from '{headers["remote_addr"]}'.");
                    return reply;
                }

                try
                {
                    var token = new JWToken(authHeader.Substring(7), m_sigUtil);

                    // TODO: Make the scope strings come from some central list that can be registered into?
                    if (!(token.HasValidSignature && token.IsNotExpired && token.Payload.Scope == "remote-console"))
                    {
                        m_log.Warn($"[REMOTECONSOLE] StartSession invalid/expired/wrong scope JWToken from '{headers["remote_addr"]}'.");
                        return reply;
                    }

                    m_log.Info($"[REMOTECONSOLE] StartSession access granted via JWT to '{token.Payload.Username}' from '{headers["remote_addr"]}'.");
                }
                catch (JWTokenException jte)
                {
                    m_log.Error($"[REMOTECONSOLE] Failure with JWToken in StartSession from '{headers["remote_addr"]}': {jte}");
                    return reply;
                }
            }
            else if (request.ContainsKey("USER") && request.ContainsKey("PASS"))
            {
                string username = post["USER"].ToString();
                string password = post["PASS"].ToString();

                // Validate the username/password pair
                if (Util.AuthenticateAsSystemUser(username, password) == false)
                    return reply;

                m_log.Warn($"[REMOTECONSOLE] StartSession access granted via legacy system username and password to '{username}' from '{headers["remote_addr"]}'.");
            }
            else
            {
                return reply;
            }

            ConsoleConnection c = new ConsoleConnection();
            c.last = System.Environment.TickCount;
            c.lastLineSeen = 0;

            UUID sessionID = UUID.Random();

            lock (m_Connections)
            {
                m_Connections[sessionID] = c;
            }

            string uri = "/ReadResponses/" + sessionID.ToString() + "/";
            IRequestHandler handler = new AsyncRequestHandler("POST", uri, AsyncReadResponses);
            m_Server.AddStreamHandler(handler);

            XmlDocument xmldoc = new XmlDocument();
            XmlNode xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration, String.Empty, String.Empty);

            xmldoc.AppendChild(xmlnode);
            XmlElement rootElement = xmldoc.CreateElement(String.Empty, "ConsoleSession", String.Empty);

            xmldoc.AppendChild(rootElement);

            XmlElement id = xmldoc.CreateElement(String.Empty, "SessionID", String.Empty);
            id.AppendChild(xmldoc.CreateTextNode(sessionID.ToString()));

            rootElement.AppendChild(id);

            XmlElement prompt = xmldoc.CreateElement(String.Empty, "Prompt", String.Empty);
            prompt.AppendChild(xmldoc.CreateTextNode(DefaultPrompt));

            rootElement.AppendChild(prompt);

            rootElement.AppendChild(MainConsole.Instance.Commands.GetXml(xmldoc));

            reply["str_response_string"] = xmldoc.InnerXml;
            reply["int_response_code"] = 200;
            reply["content_type"] = "text/xml";
            reply = CheckOrigin(reply);

            return reply;
        }

        private Hashtable HandleHttpCloseSession(Hashtable request)
        {
            DoExpire();

            Hashtable post = DecodePostString(request["body"].ToString());
            Hashtable reply = new Hashtable();

            reply["str_response_string"] = String.Empty;
            reply["int_response_code"] = 404;
            reply["content_type"] = "text/plain";

            JWToken token = null;
            var headers = (Hashtable)request["headers"];
            if (headers.ContainsKey("Authorization"))
            {
                var authHeader = headers["Authorization"].ToString();
                if (!authHeader.StartsWith("Bearer ", StringComparison.InvariantCultureIgnoreCase))
                {
                    m_log.Warn($"[REMOTECONSOLE] CloseSession JWT Authorization header format failure from '{headers["remote_addr"]}'.");
                    return reply;
                }

                try
                {
                    token = new JWToken(authHeader.Substring(7), m_sigUtil);

                    // TODO: Make the scope strings come from some central list that can be registered into?
                    if (!(token.HasValidSignature && token.IsNotExpired && token.Payload.Scope == "remote-console"))
                    {
                        m_log.Warn($"[REMOTECONSOLE] CloseSession invalid/expired/wrong scope JWToken from '{headers["remote_addr"]}'.");
                        return reply;
                    }

                    m_log.Info($"[REMOTECONSOLE] CloseSession for session '{post["ID"]}' accessed via JWT by '{token.Payload.Username}' from '{headers["remote_addr"]}'.");
                }
                catch (JWTokenException jte)
                {
                    m_log.Error($"[REMOTECONSOLE] Failure with JWToken in CloseSession from '{headers["remote_addr"]}': {jte}");
                    return reply;
                }
            }
            else
            {
                m_log.Warn($"[REMOTECONSOLE] CloseSession for session '{post["ID"]}' from '{headers["remote_addr"]}' being accessed without Authorization header!");
            }
            // BUG: Longstanding issue: if someone gets ahold of, or guesses, the ID and/or JWT of another user they can close the console.
            // The only way I can think to close this bug is to associate each session with something the user cannot change. Not sure, but maybe the IP address of the connection would work?

            if (post["ID"] == null)
                return reply;

            UUID id;
            if (!UUID.TryParse(post["ID"].ToString(), out id))
                return reply;

            lock (m_Connections)
            {
                if (m_Connections.ContainsKey(id))
                {
                    ConsoleConnection connection = m_Connections[id];
                    m_Connections.Remove(id);
                    CloseConnection(id);
                    if (connection.request != null)
                    {
                        AsyncHttpRequest req = connection.request;
                        connection.request = null;
                        req.SendResponse(ProcessEvents(req));
                    }
                }
            }

            XmlDocument xmldoc = new XmlDocument();
            XmlNode xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration, String.Empty, String.Empty);

            xmldoc.AppendChild(xmlnode);
            XmlElement rootElement = xmldoc.CreateElement(String.Empty, "ConsoleSession", String.Empty);

            xmldoc.AppendChild(rootElement);

            XmlElement res = xmldoc.CreateElement(String.Empty, "Result", String.Empty);
            res.AppendChild(xmldoc.CreateTextNode("OK"));

            rootElement.AppendChild(res);

            reply["str_response_string"] = xmldoc.InnerXml;
            reply["int_response_code"] = 200;
            reply["content_type"] = "text/xml";
            reply = CheckOrigin(reply);

            m_log.Info($"[REMOTECONSOLE] CloseSession successful for user '{token?.Payload.Username}' with session '{id}' from '{headers["remote_addr"]}'.");

            return reply;
        }

        private Hashtable HandleHttpSessionCommand(Hashtable request)
        {
            DoExpire();

            Hashtable post = DecodePostString(request["body"].ToString());
            Hashtable reply = new Hashtable();

            reply["str_response_string"] = String.Empty;
            reply["int_response_code"] = 404;
            reply["content_type"] = "text/plain";

            var headers = (Hashtable)request["headers"];
            if (headers.ContainsKey("Authorization"))
            {
                var authHeader = headers["Authorization"].ToString();
                if (!authHeader.StartsWith("Bearer ", StringComparison.InvariantCultureIgnoreCase))
                {
                    m_log.Warn($"[REMOTECONSOLE] SessionCommand JWT Authorization header format failure from '{headers["remote_addr"]}'.");
                    return reply;
                }

                try
                {
                    var token = new JWToken(authHeader.Substring(7), m_sigUtil);

                    // TODO: Make the scope strings come from some central list that can be registered into?
                    if (!(token.HasValidSignature && token.IsNotExpired && token.Payload.Scope == "remote-console"))
                    {
                        m_log.Warn($"[REMOTECONSOLE] SessionCommand invalid/expired/wrong scope JWToken from '{headers["remote_addr"]}'.");
                        return reply;
                    }

                    m_log.Info($"[REMOTECONSOLE] SessionCommand for session '{post["ID"]}' accessed via JWT by '{token.Payload.Username}' from '{headers["remote_addr"]}' with command '{post["COMMAND"]}'.");
                }
                catch (JWTokenException jte)
                {
                    m_log.Error($"[REMOTECONSOLE] Failure with JWToken in SessionCommand from '{headers["remote_addr"]}': {jte}");
                    return reply;
                }
            }
            else
            {
                m_log.Warn($"[REMOTECONSOLE] SessionCommand for session '{post["ID"]}' from '{headers["remote_addr"]}' being accessed without Authorization header!");
            }
            // BUG: Longstanding issue: if someone gets ahold of, or guesses, the ID of another user they can send comamnds to the console.
            // The only way I can think to close this bug is to associate each session with something the user cannot change. Not sure, but maybe the IP address of the connection would work?

            if (post["ID"] == null)
                return reply;

            UUID id;
            if (!UUID.TryParse(post["ID"].ToString(), out id))
                return reply;

            lock (m_Connections)
            {
                if (!m_Connections.ContainsKey(id))
                    return reply;
            }

            if (post["COMMAND"] == null)
                return reply;

            lock (m_InputData)
            {
                m_DataEvent.Set();
                m_InputData.Add(post["COMMAND"].ToString());
            }

            XmlDocument xmldoc = new XmlDocument();
            XmlNode xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration, String.Empty, String.Empty);

            xmldoc.AppendChild(xmlnode);
            XmlElement rootElement = xmldoc.CreateElement(String.Empty, "ConsoleSession", String.Empty);

            xmldoc.AppendChild(rootElement);

            XmlElement res = xmldoc.CreateElement(String.Empty, "Result", String.Empty);
            res.AppendChild(xmldoc.CreateTextNode("OK"));

            rootElement.AppendChild(res);

            reply["str_response_string"] = xmldoc.InnerXml;
            reply["int_response_code"] = 200;
            reply["content_type"] = "text/xml";
            reply = CheckOrigin(reply);

            return reply;
        }

        private Hashtable DecodePostString(string data)
        {
            Hashtable result = new Hashtable();

            string[] terms = data.Split(new char[] { '&' });

            foreach (string term in terms)
            {
                string[] elems = term.Split(new char[] { '=' });
                if (elems.Length == 0)
                    continue;

                string name = System.Web.HttpUtility.UrlDecode(elems[0]);
                string value = String.Empty;

                if (elems.Length > 1)
                    value = System.Web.HttpUtility.UrlDecode(elems[1]);

                result[name] = value;
            }

            return result;
        }

        public void CloseConnection(UUID id)
        {
            try
            {
                string uri = "/ReadResponses/" + id.ToString() + "/";
                m_Server.RemoveStreamHandler("POST", uri);
            }
            catch (Exception)
            {
            }
        }

        public void AsyncReadResponses(IHttpServer server, string path, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            int pos1 = path.IndexOf("/");               // /ReadResponses
            int pos2 = path.IndexOf("/", pos1 + 1);     // /ReadResponses/
            int pos3 = path.IndexOf("/", pos2 + 1);     // /ReadResponses/<UUID>/
            int len = pos3 - pos2 - 1;
            string uri_tmp = path.Substring(pos2 + 1, len);

            var authHeader = httpRequest.Headers.Get("Authorization");
            if (authHeader != null)
            {
                if (!authHeader.StartsWith("Bearer ", StringComparison.InvariantCultureIgnoreCase))
                {
                    m_log.Warn($"[REMOTECONSOLE] ReadResponses JWT Authorization header format failure from '{httpRequest.RemoteIPEndPoint}'.");
                    return;
                }

                try
                {
                    var token = new JWToken(authHeader.Substring(7), m_sigUtil);

                    // TODO: Make the scope strings come from some central list that can be registered into?
                    if (!(token.HasValidSignature && token.IsNotExpired && token.Payload.Scope == "remote-console"))
                    {
                        m_log.Warn($"[REMOTECONSOLE] ReadResponses invalid/expired/wrong scope JWToken from '{httpRequest.RemoteIPEndPoint}'.");
                        return;
                    }

                    m_log.Info($"[REMOTECONSOLE] ReadResponses for session '{uri_tmp}' accessed via JWT by '{token.Payload.Username}' from '{httpRequest.RemoteIPEndPoint}'.");
                }
                catch (JWTokenException jte)
                {
                    m_log.Error($"[REMOTECONSOLE] Failure with JWToken in ReadResponses from '{httpRequest.RemoteIPEndPoint}': {jte}");
                    return;
                }
            }
            else
            {
                m_log.Warn($"[REMOTECONSOLE] ReadResponses for session '{uri_tmp}' from '{httpRequest.RemoteIPEndPoint}' being accessed without Authorization header!");
            }
            // BUG: Longstanding issue: if someone gets ahold of, or guesses, the ID of another user they can send comamnds to the console.
            // The only way I can think to close this bug is to associate each session with something the user cannot change. Not sure, but maybe the IP address of the connection would work?

            UUID sessionID;
            if (UUID.TryParse(uri_tmp, out sessionID) == false)
                return;

            // Create the new request
            AsyncHttpRequest newRequest = 
                new AsyncHttpRequest(server, httpRequest, httpResponse, sessionID, TimeoutHandler, 60 * 1000);
            AsyncHttpRequest currentRequest = null;

            lock (m_Connections)
            {
                ConsoleConnection connection = null;
                m_Connections.TryGetValue(sessionID, out connection);
                if (connection == null)
                    return;

                currentRequest = connection.request;
                connection.request = newRequest;
            }

            // If there was a request already posted, signal it.
            if (currentRequest != null)
            {
                currentRequest.SendResponse(ProcessEvents(currentRequest));
            }
        }

        private void TimeoutHandler(AsyncHttpRequest pRequest)
        {
            UUID sessionid = pRequest.AgentID;

            lock (m_Connections)
            {
                ConsoleConnection connection = null;
                m_Connections.TryGetValue(sessionid, out connection);
                if (connection == null)
                    return;
                if (connection.request == pRequest)
                    connection.request = null;
            }

            pRequest.SendResponse(ProcessEvents(pRequest));
        }

        private Hashtable ProcessEvents(AsyncHttpRequest pRequest)
        {
            UUID sessionID = pRequest.AgentID;

            // Is there data to send back?  Then send it.
            if (HasEvents(pRequest.RequestID, sessionID))
                return (GetEvents(pRequest.RequestID, sessionID));
            else
                return (NoEvents(pRequest.RequestID, sessionID));
        }

        private bool HasEvents(UUID RequestID, UUID sessionID)
        {
            ConsoleConnection c = null;

            lock (m_Connections)
            {
                if (!m_Connections.ContainsKey(sessionID))
                    return false;
                c = m_Connections[sessionID];
            }
            c.last = System.Environment.TickCount;
            if (c.lastLineSeen < m_LineNumber)
                return true;

            return false;
        }

        private Hashtable GetEvents(UUID RequestID, UUID sessionID)
        {
            ConsoleConnection c = null;

            lock (m_Connections)
            {
                if (!m_Connections.ContainsKey(sessionID))
                    return NoEvents(RequestID, UUID.Zero);
                c = m_Connections[sessionID];
            }

            c.last = System.Environment.TickCount;
            if (c.lastLineSeen >= m_LineNumber)
                return NoEvents(RequestID, UUID.Zero);

            Hashtable result = new Hashtable();

            XmlDocument xmldoc = new XmlDocument();
            XmlNode xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration, String.Empty, String.Empty);

            xmldoc.AppendChild(xmlnode);
            XmlElement rootElement = xmldoc.CreateElement(String.Empty, "ConsoleSession", String.Empty);

            if (c.newConnection)
            {
                c.newConnection = false;
                Output("+++" + DefaultPrompt);
            }

            lock (m_Scrollback)
            {
                long startLine = m_LineNumber - m_Scrollback.Count;
                long sendStart = startLine;
                if (sendStart < c.lastLineSeen)
                    sendStart = c.lastLineSeen;

                for (long i = sendStart; i < m_LineNumber; i++)
                {
                    XmlElement res = xmldoc.CreateElement(String.Empty, "Line", String.Empty);
                    long line = i + 1;
                    res.SetAttribute("Number", line.ToString());
                    res.AppendChild(xmldoc.CreateTextNode(m_Scrollback[(int)(i - startLine)]));

                    rootElement.AppendChild(res);
                }
            }

            c.lastLineSeen = m_LineNumber;

            xmldoc.AppendChild(rootElement);

            result["str_response_string"] = xmldoc.InnerXml;
            result["int_response_code"] = 200;
            result["content_type"] = "application/xml";
            result["keepalive"] = false;
            result["reusecontext"] = false;
            result = CheckOrigin(result);

            return result;
        }

        private Hashtable NoEvents(UUID RequestID, UUID id)
        {
            Hashtable result = new Hashtable();

            XmlDocument xmldoc = new XmlDocument();
            XmlNode xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration,  String.Empty, String.Empty);

            xmldoc.AppendChild(xmlnode);
            XmlElement rootElement = xmldoc.CreateElement(String.Empty, "ConsoleSession", String.Empty);

            xmldoc.AppendChild(rootElement);

            result["str_response_string"] = xmldoc.InnerXml;
            result["int_response_code"] = 200;
            result["content_type"] = "text/xml";
            result["keepalive"] = false;
            result["reusecontext"] = false;
            result = CheckOrigin(result);

            return result;
        }
    }
}
