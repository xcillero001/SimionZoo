﻿using System.Collections.Generic;
using System.Linq;
using Herd;
using Caliburn.Micro;
using System;

namespace Badger.ViewModels
{
    public class ShepherdViewModel : PropertyChangedBase, IDisposable
    {
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // dispose managed resources
                m_timer.Dispose();
            }
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // const int m_agentTimeoutSeconds = 10;
        const int m_updateTimeSeconds = 3;
        System.Timers.Timer m_timer;

        private Shepherd m_shepherd;
        public Shepherd shepherd { get { return m_shepherd; } set { } }

        private object m_listsLock = new object();
        private List<HerdAgentInfo> m_innerHerdAgentList =
            new List<HerdAgentInfo>();

        private List<HerdAgentInfo> orderedHerdAgentList;

        private BindableCollection<HerdAgentViewModel> m_herdAgentList
            = new BindableCollection<HerdAgentViewModel>();
        public BindableCollection<HerdAgentViewModel> HerdAgentList
        {
            get
            {
                lock (m_listsLock)
                {
                    m_shepherd.getHerdAgentList(ref m_innerHerdAgentList);

                    // Ordering the inner list by number of processor 
                    orderedHerdAgentList = m_innerHerdAgentList.OrderByDescending(o => o.NumProcessors).ToList();

                    //Some computers send the same ProcessorId in my subnet, so, for now, I need to remove this check
                    //and use the ip address instead, which is fixed in my subnet.
                //    int len = m_herdAgentList.Count;
                //    int lenOrdered = orderedHerdAgentList.Count;

                //    // This condition and all the code inside is a temporal fix to avoid
                //    // having inactive agents in list due to IP address changing at runtime.
                //    if (len > lenOrdered)
                //    {
                //        for (int i = len - 1; i >= 0; i--)
                //        {
                //            bool found = false;
                //            int index = 0;
                //            while (!found && index < lenOrdered)
                //            {
                //                if (Equals(orderedHerdAgentList[index].ipAddress, m_herdAgentList[i].IpAddress))
                //                    found = true;
                //                index++;
                //            }

                //            if (!found)
                //                m_herdAgentList.Remove(m_herdAgentList[i]);
                //        }
                //    }

                //    len = m_herdAgentList.Count;

                //    foreach (HerdAgentInfo agent in orderedHerdAgentList)
                //    {
                //        bool found = false;
                //        int index = 0;

                //        while (!found && index < len)
                //        {
                //            if (agent.ProcessorId.Equals(m_herdAgentList[index].ProcessorId))
                //            {
                //                if (!m_herdAgentList[index].IpAddress.Equals(agent.ipAddress))
                //                    m_logFunction?.Invoke("Shepherd: Agent with Processor Id " + agent.ProcessorId + " changed IP: " + m_herdAgentList[index].IpAddress + " -> " + agent.ipAddressString);

                //                m_herdAgentList[index].IpAddress = agent.ipAddress; // The IP address can change at runtime
                //                m_herdAgentList[index].ProcessorLoad = agent.ProcessorLoad.ToString("0.") + "%";
                //                m_herdAgentList[index].State = agent.State;
                //                found = true;
                //            }

                //            index++;
                //        }

                //        if (!found)
                //        {
                //            m_herdAgentList.Add(new HerdAgentViewModel(agent));
                //            m_logFunction?.Invoke("Shepherd: Agent discovered with Processor Id " + agent.ProcessorId + " and IP address " + agent.ipAddressString);
                //        }
                //    }
                    if (orderedHerdAgentList.Count > m_herdAgentList.Count)
                    {
                        foreach(HerdAgentViewModel agentInList in m_herdAgentList)
                        {
                            bool found = false;
                            foreach(HerdAgentInfo newAgent in orderedHerdAgentList)
                            {
                                if (newAgent.ipAddressString==agentInList.IpAddressString)
                                {
                                    found = true;
                                    break;
                                }
                            }
                            if (!found)
                            {
                                m_herdAgentList.Remove(agentInList);
                                m_logFunction?.Invoke("Shepherd: Removed agent from list with Ip address " + agentInList.IpAddressString);
                            }
                        }
                    }
                    foreach (HerdAgentInfo newAgent in orderedHerdAgentList)
                    {
                        bool found = false;
                        foreach (HerdAgentViewModel agentInList in m_herdAgentList)
                        {
                            if (agentInList.IpAddressString==newAgent.ipAddressString)
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                        { 
                            m_herdAgentList.Add(new HerdAgentViewModel(newAgent));
                            m_logFunction?.Invoke("Shepherd: Agent discovered with Processor Id " + newAgent.ProcessorId + " and IP address " + newAgent.ipAddressString);
                        }
                    }
                }
                return m_herdAgentList;
            }
            set { }
        }

        public int getAvailableHerdAgents(ref List<HerdAgentViewModel> outList)
        {
            //we assume outList needs no synchronization
            int numAvailableCores = 0;
            lock (m_listsLock)
            {
                outList.Clear();
                foreach (HerdAgentViewModel agent in m_herdAgentList)
                {
                    if (agent.IsAvailable && agent.IsSelected)
                    {
                        outList.Add(agent);
                        numAvailableCores += agent.NumProcessors;
                    }
                }
            }
            return numAvailableCores;
        }

        private void NotifyHerdAgentChanged()
        {
            NotifyOfPropertyChange(() => HerdAgentList);
        }

        private void ResendBroadcast(object sender, System.Timers.ElapsedEventArgs e)
        {
            m_shepherd.sendBroadcastHerdAgentQuery();
        }

        public delegate void LogFunction(string message);

        LogFunction m_logFunction = null;
        public void SetLogFunction(LogFunction function)
        {
            m_logFunction = function;
        }

        public ShepherdViewModel()
        {
            m_shepherd = new Shepherd();
            m_shepherd.setNotifyAgentListChangedFunc(NotifyHerdAgentChanged);

            m_timer = new System.Timers.Timer(m_updateTimeSeconds * 1000);
            m_shepherd.sendBroadcastHerdAgentQuery();
            m_shepherd.beginListeningHerdAgentQueryResponses();

            m_timer.AutoReset = true;
            m_timer.Elapsed += new System.Timers.ElapsedEventHandler(ResendBroadcast);
            m_timer.Start();
        }
    }
}
