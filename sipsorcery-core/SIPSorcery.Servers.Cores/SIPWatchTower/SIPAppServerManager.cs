﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading;
using System.Xml;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using SIPSorcery.Web.Services;
using log4net;
using Microsoft.Scripting.Hosting;

namespace SIPSorcery.Servers
{
    /// <summary>
    /// 
    /// </summary>
    /// <remarks>
    /// Example call dispatcher workers node:
    /// 
    ///  <sipappserverworkers>
    ///   <sipappserverworker>
    ///     <workerprocesspath>C:\Temp\sipsorcery-appsvr1\sipsorcery-appsvr.exe</workerprocesspath>
    ///     <workerprocessargs>-sip:{0} -cms:{1}</workerprocessargs>
    ///     <sipsocket>127.0.0.1:5070</sipsocket>
    ///     <callmanageraddress>http://localhost:8081/callmanager</callmanageraddress>
    ///    </sipappserverworker>
    ///   </sipappserverworkers>
    ///   
    /// </remarks>
    public class SIPAppServerManager : ISIPCallDispatcher
    {
        private class SIPAppServerWorker
        {
            private const int START_ATTEMPT_INTERVAL = 30;

            public string WorkerProcessPath;
            public string WorkerProcessArgs;
            public SIPEndPoint AppServerEndpoint;
            public EndpointAddress CallManagerAddress;
            public Process WorkerProcess;
            public DateTime? LastStartAttempt;
            public DateTime? RestartTime { get; private set; }
            public bool HasBeenKilled;
            public bool IsUnHealthy;
            public bool InitialProbeResponse;           // Gets set to true when the first response is received from the App Server worker process.

            public event EventHandler Unhealthy;
            public event EventHandler Healthy;

            public SIPAppServerWorker(XmlNode xmlConfigNode)
            {
                WorkerProcessPath = xmlConfigNode.SelectSingleNode("workerprocesspath").InnerText;
                WorkerProcessArgs = xmlConfigNode.SelectSingleNode("workerprocessargs").InnerText;
                AppServerEndpoint = SIPEndPoint.ParseSIPEndPoint(xmlConfigNode.SelectSingleNode("sipsocket").InnerText);
                CallManagerAddress = new EndpointAddress(xmlConfigNode.SelectSingleNode("callmanageraddress").InnerText);
            }

            public string StartProcess()
            {
                if (LastStartAttempt == null || DateTime.Now.Subtract(LastStartAttempt.Value).TotalSeconds > START_ATTEMPT_INTERVAL)
                {
                    if (!HasBeenKilled && WorkerProcess != null)
                    {
                        Kill();
                    }

                    LastStartAttempt = DateTime.Now;
                    RestartTime = null;
                    HasBeenKilled = false;
                    IsUnHealthy = false;
                    ProcessStartInfo startInfo = new ProcessStartInfo(WorkerProcessPath, String.Format(WorkerProcessArgs, new object[] { AppServerEndpoint.ToString(), CallManagerAddress.ToString(), "false" }));
                    startInfo.CreateNoWindow = true;
                    startInfo.UseShellExecute = false;
                    WorkerProcess = Process.Start(startInfo);
                    logger.Debug("New call dispatcher worker process on " + AppServerEndpoint.ToString() + " started on pid=" + WorkerProcess.Id + ".");

                    //if (Healthy != null)
                    //{
                    //    Healthy(this, null);
                    //}

                    return null;
                }
                else
                {
                    return "Delaying worker process " + AppServerEndpoint.ToString() + " restart for " + (START_ATTEMPT_INTERVAL - DateTime.Now.Subtract(LastStartAttempt.Value).TotalSeconds).ToString("0.00") + "s.";
                }
            }

            public void InitialCallSuccessful()
            {
                InitialProbeResponse = true;
                Healthy(this, null);
            }

            public void ScheduleRestart(DateTime restartTime)
            {
                RestartTime = restartTime;
                if (Unhealthy != null)
                {
                    Unhealthy(this, null);
                }
            }

            private void Kill()
            {
                try
                {
                    logger.Debug("Killing worker process on pid=" + WorkerProcess.Id + ".");
                    WorkerProcess.Kill();
                    HasBeenKilled = true;
                }
                catch (Exception excp)
                {
                    logger.Error("Exception SIPAppServerWorker Kill. " + excp.Message);
                }
            }

            public bool IsHealthy()
            {
                try
                {
                    if (WorkerProcess != null && !WorkerProcess.HasExited && RestartTime == null)
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                catch (Exception excp)
                {
                    logger.Error("Exception SIPAppServerWorker IsHealthy. " + excp.Message);
                    return false;
                }
            }
        }

        private const string WORKER_PROCESS_MONITOR_THREAD_NAME = "sipappservermanager-workermonitor";
        private const string WORKER_PROCESS_PROBE_THREAD_NAME = "sipappservermanager-probe";

        private const int MAX_LIFETIME_SECONDS = 180;
        private const long MAX_PHYSICAL_MEMORY = 150000000; // Restart worker processes when they've used up 150MB of physical memory.
        private const int PROCESS_RESTART_DELAY = 33;
        private const int CHECK_WORKER_MEMORY_PERIOD = 1000;
        private const int PROBE_WORKER_CALL_PERIOD = 15000;
        private const int PROCESS_INITIALISATION_PERIOD = 30000;    // The time to allow for a restarted application server process to start before sending calls to it.

        private static int m_unhealthyPriority = SIPCallDispatcherFile.DISABLED_APPSERVER_PRIORITY;
        private static int m_healthyPriority = SIPCallDispatcherFile.USEALWAYS_APPSERVER_PRIORITY;

        private static ILog logger = AppState.logger;

        private SIPMonitorLogDelegate SIPMonitorLogEvent_External;
        private SIPTransport m_sipTransport;
        private XmlNode m_appServerWorkersNode;
        private ServiceHost m_callManagerPassThruSvcHost;
        private bool m_exit;
        private string m_dispatcherUsername = SIPCallManager.DISPATCHER_SIPACCOUNT_NAME;
        private string m_appServerEndPointsPath;
        private SIPCallDispatcherFile m_sipCallDispatcherFile;

        private List<SIPAppServerWorker> m_appServerWorkers = new List<SIPAppServerWorker>();
        private List<string> m_workerSIPEndPoints = new List<string>();                             // Allow quick lookups to determine whether a remote end point is that of a worker process.

        public SIPAppServerManager(
            SIPMonitorLogDelegate logDelegate,
            SIPTransport sipTransport,
            XmlNode appServerWorkersNode,
            string appServerEndPointsPath)
        {
            if (appServerWorkersNode == null || appServerWorkersNode.ChildNodes.Count == 0)
            {
                throw new ArgumentNullException("A SIPAppServerManager cannot be created with an empty workers node.");
            }

            SIPMonitorLogEvent_External = logDelegate;
            m_sipTransport = sipTransport;
            m_appServerWorkersNode = appServerWorkersNode;
            m_appServerEndPointsPath = appServerEndPointsPath;

            if (!appServerEndPointsPath.IsNullOrBlank() && File.Exists(appServerEndPointsPath))
            {
                m_sipCallDispatcherFile = new SIPCallDispatcherFile(logDelegate, appServerEndPointsPath);
            }

            try
            {
                CallManagerPassThruServiceInstanceProvider callManagerPassThruSvcInstanceProvider = new CallManagerPassThruServiceInstanceProvider(this);
                m_callManagerPassThruSvcHost = new ServiceHost(typeof(CallManagerPassThruService));
                m_callManagerPassThruSvcHost.Description.Behaviors.Add(callManagerPassThruSvcInstanceProvider);
                m_callManagerPassThruSvcHost.Open();

                logger.Debug("SIPAppServerManager CallManagerPassThru hosted service successfully started on " + m_callManagerPassThruSvcHost.BaseAddresses[0].AbsoluteUri + ".");
            }
            catch (Exception excp)
            {
                logger.Warn("Exception starting SIPAppServerManager CallManagerPassThru hosted service. " + excp.Message);
            }

            foreach (XmlNode appServerWorkerNode in m_appServerWorkersNode.ChildNodes)
            {
                SIPAppServerWorker appServerWorker = new SIPAppServerWorker(appServerWorkerNode);
                if (m_sipCallDispatcherFile != null)
                {
                    appServerWorker.Healthy += (s, e) => 
                    {
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            Thread.Sleep(PROCESS_INITIALISATION_PERIOD);
                            if (appServerWorker.IsHealthy())
                            {
                                m_sipCallDispatcherFile.UpdateAppServerPriority(((SIPAppServerWorker)s).AppServerEndpoint, m_healthyPriority);
                            }
                            else
                            {
                                logger.Warn("An app server worker was unhealthy after the process initialisation period.");
                            }
                        });
                    };
                    appServerWorker.Unhealthy += (s, e) => { m_sipCallDispatcherFile.UpdateAppServerPriority(((SIPAppServerWorker)s).AppServerEndpoint, m_unhealthyPriority); };
                }
                m_appServerWorkers.Add(appServerWorker);
                m_workerSIPEndPoints.Add(appServerWorker.AppServerEndpoint.ToString());
                logger.Debug(" SIPAppServerManager worker added for " + appServerWorker.AppServerEndpoint.ToString() + " and " + appServerWorker.CallManagerAddress.ToString() + ".");
            }

            ThreadPool.QueueUserWorkItem(delegate { SpawnWorkers(); });
            ThreadPool.QueueUserWorkItem(delegate { ProbeWorkers(); });
        }

        private void SpawnWorkers()
        {
            try
            {
                Thread.CurrentThread.Name = WORKER_PROCESS_MONITOR_THREAD_NAME;

                foreach (SIPAppServerWorker worker in m_appServerWorkers)
                {
                    StartWorkerProcess(worker);
                }

                while (!m_exit)
                {
                    try
                    {
                        lock (m_appServerWorkers)
                        {
                            foreach (SIPAppServerWorker worker in m_appServerWorkers)
                            {
                                if (worker.InitialProbeResponse)
                                {
                                    if (worker.RestartTime != null)
                                    {
                                        if (worker.RestartTime < DateTime.Now)
                                        {
                                            StartWorkerProcess(worker);
                                        }
                                    }
                                    else if (!worker.IsHealthy())
                                    {
                                        if (!worker.IsUnHealthy)
                                        {
                                            worker.IsUnHealthy = true;
                                            m_sipCallDispatcherFile.UpdateAppServerPriority(worker.AppServerEndpoint, m_unhealthyPriority);
                                        }
                                        StartWorkerProcess(worker);
                                    }
                                    else
                                    {
                                        worker.WorkerProcess.Refresh();
                                        if (worker.WorkerProcess.PrivateMemorySize64 >= MAX_PHYSICAL_MEMORY)
                                        {
                                            // If there is another worker already scheduled for a restart then don't schedule another one until it's restarted.
                                            if ((from wk in m_appServerWorkers where wk.RestartTime != null select wk).Count() == 0)
                                            {
                                                logger.Debug("Worker process on pid=" + worker.WorkerProcess.Id + " has reached the memory limit, scheduling a restart.");
                                                worker.ScheduleRestart(DateTime.Now.AddSeconds(PROCESS_RESTART_DELAY));
                                            }
                                            else
                                            {
                                                logger.Debug("Worker process on pid=" + worker.WorkerProcess.Id + " has reached the memory limit but a restart was NOT scheduled due to another worker already being scheduled.");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception checkWorkersExcp)
                    {
                        logger.Error("Exception SIPAppServerManager Checking Workers. " + checkWorkersExcp.Message);
                    }

                    Thread.Sleep(CHECK_WORKER_MEMORY_PERIOD);
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception SIPAppServerManager SpawnWorkers. " + excp.Message);
            }
        }

        private void StartWorkerProcess(SIPAppServerWorker worker)
        {
            string errorMessage = worker.StartProcess();
            if (errorMessage == null)
            {
                ProbeWorker(worker.AppServerEndpoint);
            }
            else
            {
                logger.Warn("Error starting worker process for " + worker.AppServerEndpoint.ToString() + ". " + errorMessage);
            }
        }

        private void ProbeWorkers()
        {
            try
            {
                while (!m_exit)
                {
                    Thread.Sleep(PROBE_WORKER_CALL_PERIOD);

                    try
                    {
                        SIPEndPoint activeWorkerEndPoint = GetFirstHealthyEndPoint();
                        if (activeWorkerEndPoint != null)
                        {
                            ProbeWorker(activeWorkerEndPoint);
                        }
                        else
                        {
                            logger.Warn("SIPAppServerManager was not able to find a healthy app server endpoint.");
                        }

                        lock (m_appServerWorkers)
                        {
                            foreach (SIPAppServerWorker worker in m_appServerWorkers)
                            {
                                if (!worker.InitialProbeResponse)
                                {
                                    logger.Debug("Resending initial probe to " + worker.AppServerEndpoint.ToString() + ".");
                                    ProbeWorker(worker.AppServerEndpoint);
                                }
                            }
                        }
                    }
                    catch (Exception probeExcp)
                    {
                        logger.Error("Exception SIPAppServerManager Sending Probe. " + probeExcp.Message);
                    }
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception SIPAppServerManager ProberWorkers. " + excp.Message);
            }
        }

        private void ProbeWorker(SIPEndPoint workerEndPoint)
        {
            try
            {
                SIPCallDescriptor callDescriptor = new SIPCallDescriptor(m_dispatcherUsername, null, "sip:" + m_dispatcherUsername + "@" + workerEndPoint.GetIPEndPoint().ToString(),
                                   "sip:" + m_dispatcherUsername + "@sipcalldispatcher", "sip:" + workerEndPoint.GetIPEndPoint().ToString(), null, null, null, SIPCallDirection.Out, null, null, null);
                SIPClientUserAgent uac = new SIPClientUserAgent(m_sipTransport, null, null, null, null);
                uac.CallFailed += new SIPCallFailedDelegate(AppServerCallFailed);
                uac.CallAnswered += (call, sipResponse) =>
                {
                    if (sipResponse.Status != SIPResponseStatusCodesEnum.BadExtension)
                    {
                        //logger.Warn("Probe call answered with unexpected response code of " + sipResponse.StatusCode + ".");
                        AppServerCallFailed(call, "Unexpected response of " + ((int)sipResponse.StatusCode) + " on probe call.");
                    }
                    else
                    {
                        AppServerCallSucceeded(call);
                    }
                };
                uac.Call(callDescriptor);
            }
            catch (Exception excp)
            {
                logger.Error("Exception SIPAppServerManager ProberWorker. " + excp.Message);
            }
        }

        private void AppServerCallSucceeded(ISIPClientUserAgent uac)
        {
            try
            {
                string workerSocket = SIPURI.ParseSIPURI(uac.CallDescriptor.Uri).Host;
                SIPAppServerWorker worker = GetWorkerForEndPoint(workerSocket);
                if (!worker.InitialProbeResponse)
                {
                    worker.InitialCallSuccessful();
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception AppServerCallSucceeded. " + excp.Message); 
            }
        }

        private void AppServerCallFailed(ISIPClientUserAgent uac, string errorMessage)
        {
            try
            {
                string workerSocket = SIPURI.ParseSIPURI(uac.CallDescriptor.Uri).Host;
                logger.Warn("SIPAppServerManager call to " + workerSocket + " failed " + errorMessage);

                // Find the worker for the failed end point.
                SIPAppServerWorker failedWorker = GetWorkerForEndPoint(workerSocket);
               
                if (failedWorker != null)
                {
                    failedWorker.InitialProbeResponse = true;
                    logger.Debug("Scheduling immediate restart on app server worker process pid=" + failedWorker.WorkerProcess.Id + ", " + workerSocket + " due to failed probe.");
                    failedWorker.ScheduleRestart(DateTime.Now);
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception AppServerCallFailed. " + excp.Message);
            }
        }

        private SIPAppServerWorker GetWorkerForEndPoint(string host)
        {
            lock (m_appServerWorkers)
            {
                foreach (SIPAppServerWorker worker in m_appServerWorkers)
                {
                    if (worker.AppServerEndpoint.GetIPEndPoint().ToString() == host)
                    {
                        return worker;
                    }
                }
            }

            return null;
        }

        public void Stop()
        {
            m_exit = true;
        }

        public CallManagerProxy GetCallManagerClient()
        {
            lock (m_appServerWorkers)
            {
                foreach (SIPAppServerWorker worker in m_appServerWorkers)
                {
                    if (worker.IsHealthy())
                    {
                        return new CallManagerProxy(new BasicHttpBinding(), worker.CallManagerAddress);
                    }
                }
            }

            logger.Warn("GetCallManagerClient could not find a healthy SIPAppServerWorker.");

            return null;
        }

        public SIPEndPoint GetFirstHealthyEndPoint()
        {
            lock (m_appServerWorkers)
            {
                foreach (SIPAppServerWorker worker in m_appServerWorkers)
                {
                    if (worker.IsHealthy())
                    {
                        return worker.AppServerEndpoint;
                    }
                }
            }

            logger.Warn("GetFirstHealthyEndPoint could not find a healthy SIPAppServerWorker.");

            return null;
        }
    }
}
