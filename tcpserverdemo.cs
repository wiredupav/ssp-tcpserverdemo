using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Crestron.SimplSharp;                          				// For Basic SIMPL# Classes
using Crestron.SimplSharp.CrestronSockets;
using Crestron.SimplSharpPro;                       				// For Basic SIMPL#Pro classes
using Crestron.SimplSharpPro.CrestronThread;        	// For Threading
using Crestron.SimplSharpPro.DeviceSupport;         	// For Generic Device Support
using Crestron.SimplSharpPro.UI;
using Crestron.SimplSharpPro.EthernetCommunication;
using Crestron.SimplSharpProInternal;
using Crestron.SimplSharp.CrestronLogger;
using Newtonsoft.Json;


namespace FrameWork
{
    // RTI Interface
    public class TCPServerDemo : IDisposable
    {
        // Fields
        private static bool _StoppingFlag = false;
        protected TCPServer _TCPServer;
        protected CTimer ReconnectTimer;
        private IPEndPoint myIP;
        private CrestronQueue<string> txMsgs, rxMsgs;
        private Thread _tSendData, _tRxProcessor;
        private int _tSendDataIL, _tRXProcessorIL;

        // Methods
        #region SendStuff
        private object tSendData(object sender)
        {
            try
            {
                while (true)
                {
                    while (!_StoppingFlag)
                    {
                        string tempmsg = null;
                        try
                        {
                            tempmsg = txMsgs.Dequeue(10000);
                            //CrestronConsole.PrintLine("tSendData Dequeued");

                            if (tempmsg != null)
                                CrestronInvoke.BeginInvoke(new CrestronSharpHelperDelegate(o => InvokeSendData(tempmsg)));

                            if (txMsgs.IsEmpty)
                            {
                                //CrestronConsole.PrintLine("TX Thread returning - nothing in queue");
                                _tSendDataIL = 0;
                                return null;
                            }
                        }
                        catch
                        {
                            _tSendDataIL = 0;
                        }
                    }
                }
            }
            finally
            {
                _tSendDataIL = 0;
            }
        }

        private void InvokeSendData(string tempmsg)
        {
            SocketErrorCodes err;
            byte[] tempByteArray = UTF8Encoding.ASCII.GetBytes(tempmsg + "\r\n");
            try
            {
                err = _TCPServer.SendData(tempByteArray, tempByteArray.Length);
            }
            catch (Exception e)
            {
                ErrorLog.Exception("Send data exception", e);
                return;
            }
        }

        private void SendData(string msg)
        {
            if (txMsgs.IsFull)
            {
                CrestronConsole.PrintLine("can't enqueue tx - queue full");
            }
            else
            {
                txMsgs.Enqueue(msg);
            }
            CheckThreadState(ref _tSendData, ref _tSendDataIL, tSendData, "tSendData");
        }
        #endregion

        private void CheckThreadState(ref Thread thread, ref int threadIL, Crestron.SimplSharpPro.CrestronThread.ThreadCallbackFunction method, string methodname)
        {
            if (fw.EventsEnabled)
            {
                if (Interlocked.CompareExchange(ref threadIL, 1, 0) == 0)
                {
                    thread = new Thread(method, null, Thread.eThreadStartOptions.Running);
                }
                else
                {
                    //CrestronConsole.PrintLine("- didn't start new " + methodname + " - thread already running");
                }
            }
        }

        private void RXProcessor(TCPServer server, uint clientIndex, int size)
        {
            byte[] rxd;
            string rxmsg;
            string[] rxmsgs;

            try
            {
                if (size < 0)
                {
                    ErrorLog.Error("Disconnceted");
                    return;
                }
                else if (size == 0)
                {
                    ErrorLog.Error("0 length data");
                    return;
                }

                rxd = server.GetIncomingDataBufferForSpecificClient(clientIndex);

                // If buffer empty, quit
                if (rxd == null)
                {
                    return;
                }
                if (rxd.Length == 0)
                {
                    return;
                }

                // Convert bytes to ascii
                rxmsg = Encoding.ASCII.GetString(rxd, 0, size);

                // Split to array, then enqueue
                rxmsgs = rxmsg.Split('\r');
                foreach (string i in rxmsgs)
                {
                    rxMsgs.Enqueue(i);
                }
                CheckThreadState(ref _tRxProcessor, ref _tRXProcessorIL, tRxHandler, "tRxHandler");
                server.ReceiveDataAsync(clientIndex, new TCPServerReceiveCallback(RXProcessor));
            }
            catch (Exception e)
            {
                if (e.Message.Contains("ThreadAbortException"))
                {
                    Terminate();
                }
                else
                {
                    ErrorLog.Exception("Error parsing packet:", e);
                }
            }

            rxmsgs = null;
        }

        private object tRxHandler(object obj)
        {
            try
            {
                while (true)
                {
                    string rxmsg = null;
                    try
                    {
                        rxmsg = rxMsgs.Dequeue(10000);
                        if (rxmsg != null)
                            CrestronInvoke.BeginInvoke(new CrestronSharpHelperDelegate(func => InvokeRxProcessor(rxmsg)));
                        if (rxMsgs.IsEmpty)
                        {
                            CrestronConsole.PrintLine("RX Thread returning - nothing in queue");
                            _tRXProcessorIL = 0;
                            return null;
                        }
                    }
                    catch
                    {
                        CrestronConsole.PrintLine("RX Thread - exception dequeueing");
                        _tRXProcessorIL = 0;
                        return null;
                    }
                }
            }
            finally
            {
                _tRXProcessorIL = 0;
            }
        }

        private void InvokeRxProcessor(string rxmsg)
        {
            if (rxmsg == "")
                return;                    
            
            if (rxmsg == null)
                return;
            
            if (rxmsg == "\r")
                return;

            try
            {
                // Do processing
            }
            catch (Exception e)
            {
                ErrorLog.Exception("Caught exception whilst processing JSON:", e);
                return;
            }            
        }

        // Server Handlers
        protected void ConnectProcessor(TCPServer server, uint clientindex)
        {
            ErrorLog.Notice("Connection accepted from processor");
            server.ReceiveDataAsync(clientindex, new TCPServerReceiveCallback(RXProcessor));
        }

        protected void ReconnectCallback(object sender)
        {
            try
            {
                _TCPServer.WaitForConnectionAsync(new TCPServerClientConnectCallback(ConnectProcessor));
                ErrorLog.Notice("waiting for connection");
            }
            catch (Exception e)
            {
                ErrorLog.Exception("Exception occured in Reconnect Callback", e);
            }
        }

        private void SocketStatusHandler(TCPServer server, uint clientindex, SocketStatus status)
        {
            ErrorLog.Notice("TCPServer Socket Status Change Handler. Status = " + status.ToString());
            if (status != SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                if (Thread.CurrentThread.ThreadState == Thread.eThreadStates.ThreadAborting)
                {
                    // Thread aborted so exit
                    return;
                }

                ErrorLog.Notice("Starting reconnect timer");
                ReconnectTimer = new CTimer(new CTimerCallbackFunction(ReconnectCallback), 5000);
            }
            return;
        }

        private void Terminate()
        {
            _StoppingFlag = true;
            _TCPServer.SocketStatusChange -= SocketStatusHandler;
            _TCPServer.DisconnectAll();
            _TCPServer.Stop();
        }
        private void HandleProgramEvent(eProgramStatusEventType programStatusEventType)
        {
            if ((programStatusEventType == eProgramStatusEventType.Stopping) || (programStatusEventType == eProgramStatusEventType.Paused))
            {
                try { Terminate(); return; }
                catch (Exception e) { ErrorLog.Exception("Exception trying to terminate due to program stop.", e); return; };
            }
        }
        private void HandleSystemEvent(eSystemEventType systemEventType)
        {
            if (systemEventType == eSystemEventType.Rebooting)
            {
                try { Terminate(); return; }
                catch (Exception e) { ErrorLog.Exception("Exception trying to terminate due to reboot.", e); return; };
            }
        }

        // Constructor
        public TCPServerDemo(IPEndPoint ip)
        {
            CrestronEnvironment.ProgramStatusEventHandler += new ProgramStatusEventHandler(this.HandleProgramEvent);
            CrestronEnvironment.SystemEventHandler += new SystemEventHandler(this.HandleSystemEvent);

            this.myIP = ip;
            txMsgs = new CrestronQueue<string>(50);
            rxMsgs = new CrestronQueue<string>(50);

            _TCPServer = new TCPServer("0.0.0.0", ip.Port, 1000);
            _TCPServer.WaitForConnectionAsync(new TCPServerClientConnectCallback(ConnectProcessor));

            _TCPServer.SocketStatusChange += new TCPServerSocketStatusChangeEventHandler(SocketStatusHandler);

            _tRXProcessorIL = 0;
            _tSendDataIL = 0;
        }

        public void Dispose()
        {
            Terminate();
        }

        ~TCPServerDemo()
        {
            Terminate();
        }
    }
}