﻿using ExtensionMethods;
using Microsoft.Win32;
using SharpHound.DatabaseObjects;
using SharpHound.OutputObjects;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.DirectoryServices;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SharpHound
{
    class SessionEnumeration
    {
        Helpers helpers;
        Options options;
        DBManager manager;

        static int count;
        static int dead;
        static int total;
        static string CurrentDomain;
        static string CurrentUser;
        static string GCPath;

        static ConcurrentDictionary<string, DBObject> sidmap;
        static ConcurrentDictionary<string, string> ResolveCache;

        public SessionEnumeration()
        {
            helpers = Helpers.Instance;
            options = helpers.Options;
            manager = DBManager.Instance;
            sidmap = new ConcurrentDictionary<string, DBObject>();
            CurrentUser = Environment.UserName;
            ResolveCache = new ConcurrentDictionary<string, string>();
            GCPath = $"GC://{new DirectoryEntry("LDAP://RootDSE").Properties["dnshostname"].Value.ToString()}";
        }

        public void StartEnumeration()
        {
            Console.WriteLine("\nStarting Session Enumeration");
            List<string> Domains = helpers.GetDomainList();
            Stopwatch watch = Stopwatch.StartNew();
            Stopwatch overwatch = Stopwatch.StartNew();
            foreach (string DomainName in Domains)
            {
                CurrentDomain = DomainName;
                count = 0;
                BlockingCollection<Computer> input = new BlockingCollection<Computer>();
                BlockingCollection<SessionInfo> output = new BlockingCollection<SessionInfo>();

                LimitedConcurrencyLevelTaskScheduler scheduler = new LimitedConcurrencyLevelTaskScheduler(options.Threads);
                TaskFactory factory = new TaskFactory(scheduler);
                List<Task> taskhandles = new List<Task>();

                Task writer = StartWriter(output, factory);
                for (int i = 0; i < options.Threads; i++)
                {
                    taskhandles.Add(StartConsumer(input, output, factory));
                }

                if (options.Stealth)
                {
                    Console.WriteLine($"Started stealth session enumeration for {DomainName}");
                    var users = manager.GetUsers().Find(x => x.HomeDirectory != null || x.ScriptPath != null || x.ProfilePath != null);
                    ConcurrentBag<string> paths = new ConcurrentBag<string>();
                    Parallel.ForEach(users, (result) =>
                    {
                        string home = result.HomeDirectory;
                        string script = result.ScriptPath;
                        string prof = result.ProfilePath;

                        if (home != null)
                        {
                            paths.Add(home.ToLower().Split('\\')[2]);
                        }

                        if (script != null)
                        {
                            paths.Add(script.ToLower().Split('\\')[2]);
                        }

                        if (prof != null)
                        {
                            paths.Add(prof.ToLower().Split('\\')[2]);
                        }
                    });

                    foreach (string key in paths)
                    {
                        if (!ResolveCache.TryGetValue(key, out string hostname))
                        {
                            try
                            {
                                hostname = System.Net.Dns.GetHostEntry(key).HostName;
                                ResolveCache.TryAdd(key, hostname);
                            }
                            catch
                            {
                                continue;
                            }
                        }
                        if (hostname == null)
                        {
                            continue;
                        }
                        input.Add(manager.GetComputers().FindOne(x => x.DNSHostName.ToUpper().Equals(hostname)));
                    }
                    input.CompleteAdding();
                    Task.WaitAll(taskhandles.ToArray());
                    output.CompleteAdding();
                    writer.Wait();
                    continue;
                }

                Console.WriteLine($"Started session enumeration for {DomainName}");
                var computers =
                    manager.GetComputers().Find(x => x.Domain.Equals(DomainName));

                System.Timers.Timer t = new System.Timers.Timer();
                t.Elapsed += new System.Timers.ElapsedEventHandler(Timer_Tick);

                t.Interval = options.Interval;
                t.Enabled = true;

                total = computers.Count();
                PrintStatus();
                foreach (Computer c in computers)
                {
                    input.Add(c);
                }
                input.CompleteAdding();
                options.WriteVerbose("Waiting for enumeration threads to finish...");
                Task.WaitAll(taskhandles.ToArray());
                output.CompleteAdding();
                options.WriteVerbose("Waiting for writer thread to finish...");
                writer.Wait();
                PrintStatus();
                t.Dispose();
                Console.WriteLine($"Enumeration for {CurrentDomain} done in {watch.Elapsed}");
                watch.Reset();
            }
            Console.WriteLine($"Local Admin Enumeration done in {overwatch.Elapsed}");
            watch.Stop();
            overwatch.Stop();
        }

        Task StartWriter(BlockingCollection<SessionInfo> output, TaskFactory factory)
        {
            return factory.StartNew(() =>
            {
                if (options.URI == null)
                {
                    string path = options.GetFilePath("sessions");
                    bool append = false || File.Exists(path);
                    using (StreamWriter writer = new StreamWriter(path, append))
                    {
                        if (!append)
                        {
                            writer.WriteLine("UserName, ComputerName, Weight");
                        }
                        writer.AutoFlush = true;
                        foreach (SessionInfo info in output.GetConsumingEnumerable())
                        {
                            writer.WriteLine(info.ToCSV());
                        }
                    }
                }
            });
        }

        Task StartConsumer(BlockingCollection<Computer> input, BlockingCollection<SessionInfo> output, TaskFactory factory)
        {
            return factory.StartNew(() =>
            {
                Helpers _helper = Helpers.Instance;
                foreach (Computer c in input.GetConsumingEnumerable())
                {
                    string hostname = c.DNSHostName;
                    if (!_helper.PingHost(hostname))
                    {
                        _helper.Options.WriteVerbose($"{hostname} did not respond to ping");
                        Interlocked.Increment(ref dead);
                        continue;
                    }

                    List<SessionInfo> sessions;
                    if (_helper.Options.CollMethod.Equals(Options.CollectionMethod.LoggedOn))
                    {
                        sessions = GetNetLoggedOn(hostname, c.SAMAccountName);
                        sessions.AddRange(GetLocalLoggedOn(hostname, c.SAMAccountName));
                    }
                    else
                    {
                        sessions = GetNetSessions(hostname, c.Domain);
                    }
                    Interlocked.Increment(ref count);
                    sessions.ForEach(output.Add);
                }
            });
        }

        #region Helpers
        List<SessionInfo> GetNetLoggedOn(string server, string SaMAccountName)
        {
            List<SessionInfo> results = new List<SessionInfo>();

            int QueryLevel = 1;
            IntPtr info = IntPtr.Zero;
            int ResumeHandle = 0;

            Type tWui1 = typeof(WKSTA_USER_INFO_1);

            int result = NetWkstaUserEnum(server, QueryLevel, out info, -1, out int EntriesRead, out int TotalRead, ref ResumeHandle);
            long offset = info.ToInt64();

            if (result == 0 || result == 234)
            {
                IntPtr iter = info;
                for (int i = 0; i < EntriesRead; i++)
                {
                    WKSTA_USER_INFO_1 data = (WKSTA_USER_INFO_1)Marshal.PtrToStructure(iter, tWui1);
                    iter = (IntPtr)(iter.ToInt64() + Marshal.SizeOf(tWui1));
                    string username = data.wkui1_username;
                    string domain = data.wkui1_logon_domain;
                    string servername = server.Split('.')[0].ToUpper();

                    if (username.Trim() == "" || username.EndsWith("$", StringComparison.CurrentCulture) || SaMAccountName.Equals(domain))
                    {
                        continue;
                    }

                    string MemberName;

                    if (Helpers.DomainMap.TryGetValue(domain, out string resolved))
                    {
                        MemberName = $"{username.ToUpper()}@{domain}";
                    }
                    else
                    {
                        resolved = helpers.GetDomain(domain).Name;
                        Helpers.DomainMap.TryAdd(domain, resolved);
                        MemberName = $"{username.ToUpper()}@{domain}";
                    }

                    results.Add(new SessionInfo()
                    {
                        ComputerName = server,
                        UserName = MemberName,
                        Weight = 1
                    });
                }
            }
            NetApiBufferFree(info);

            return results;
        }

        List<SessionInfo> GetLocalLoggedOn(string server, string SaMAccountName)
        {
            List<SessionInfo> results = new List<SessionInfo>();

            try
            {
                RegistryKey key;
                if (Environment.MachineName.Equals(server.Split('.')[0]))
                {
                    key = RegistryKey.OpenRemoteBaseKey(RegistryHive.Users, "");
                }
                else
                {
                    key = RegistryKey.OpenRemoteBaseKey(RegistryHive.Users, server);
                }

                var filtered = key.GetSubKeyNames().Where(sub => Regex.IsMatch(sub, "S-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+$"));

                foreach (var x in filtered)
                {
                    if (manager.FindBySID(x, CurrentDomain, out DBObject resolved))
                    {
                        results.Add(new SessionInfo()
                        {
                            ComputerName = server,
                            UserName = resolved.BloodHoundDisplayName,
                            Weight = 1
                        });
                    }
                    else
                    {
                        try
                        {
                            DirectoryEntry entry = new DirectoryEntry($"LDAP://<SID={x}>");
                            resolved = entry.ConvertToDB();
                            manager.InsertRecord(resolved);


                        }
                        catch
                        {
                            string converted = helpers.ConvertSIDToName(x);
                            if (!converted.Contains("\\"))
                            {
                                continue;
                            }
                            var parts = converted.Split('\\');
                            string username = parts[1];
                            string domain = parts[0];

                            if (domain.ToUpper().Equals(SaMAccountName))
                            {
                                continue;
                            }

                            if (!Helpers.DomainMap.TryGetValue(domain, out string fulldomain))
                            {
                                fulldomain = helpers.GetDomain(domain).Name;
                                Helpers.DomainMap.TryAdd(domain, fulldomain);
                            }

                            resolved = new DBObject
                            {
                                BloodHoundDisplayName = $"{username}@{fulldomain}"
                            };
                        }
                    }
                    results.Add(new SessionInfo()
                    {
                        ComputerName = server,
                        UserName = resolved.BloodHoundDisplayName,
                        Weight = 1
                    });
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
            return results;
        }

        List<SessionInfo> GetNetSessions(string server, string ComputerDomain)
        {
            List<SessionInfo> toReturn = new List<SessionInfo>();
            IntPtr PtrInfo = IntPtr.Zero;
            int val;
            IntPtr ResumeHandle = IntPtr.Zero;

            Type si10 = typeof(SESSION_INFO_10);

            val = NetSessionEnum(server, null, null, 10, out PtrInfo, -1, out int EntriesRead, out int TotalRead, ref ResumeHandle);

            SESSION_INFO_10[] results = new SESSION_INFO_10[EntriesRead];

            if (val == (int)NERR.NERR_Success)
            {
                IntPtr iter = PtrInfo;
                for (int i = 0; i < EntriesRead; i++)
                {
                    results[i] = (SESSION_INFO_10)Marshal.PtrToStructure(iter, si10);
                    iter = (IntPtr)(iter.ToInt64() + Marshal.SizeOf(si10));
                }
            }

            NetApiBufferFree(PtrInfo);

            foreach (SESSION_INFO_10 x in results)
            {
                string username = x.sesi10_username;
                string cname = x.sesi10_cname;

                if (cname != null && cname.StartsWith("\\", StringComparison.CurrentCulture))
                {
                    cname = cname.TrimStart('\\');
                }

                if (cname.Equals("[::1]"))
                {
                    cname = server;
                }

                if (username.EndsWith("$", StringComparison.CurrentCulture))
                {
                    continue;
                }

                if (username.Trim() == "" || username == "$" || username == CurrentUser)
                {
                    continue;
                }

                if (!ResolveCache.TryGetValue(cname, out string DNSHostName))
                {
                    DNSHostName = System.Net.Dns.GetHostEntry(cname).HostName;
                    ResolveCache.TryAdd(cname, DNSHostName);
                }

                GlobalCatalogMap obj;
                if (options.SkipGCDeconfliction)
                {
                    obj = new GlobalCatalogMap
                    {
                        Username = username,
                        PossibleNames = new List<string>()
                    };
                }
                else
                {
                    if (!manager.GetGCMap(username, out obj))
                    {
                        DirectorySearcher GCSearcher = helpers.GetDomainSearcher(ADSPath: GCPath);
                        GCSearcher.Filter = $"(&(samAccountType=805306368)(samaccountname={username}))";
                        GCSearcher.PropertiesToLoad.AddRange(new string[] { "distinguishedname" });
                        List<string> possible = new List<string>();
                        foreach (SearchResult r in GCSearcher.FindAll())
                        {
                            string dn = r.GetProp("distinguishedname");
                            string domain = Helpers.DomainFromDN(dn);
                            possible.Add(domain.ToUpper());
                        }
                        GCSearcher.Dispose();
                        obj = new GlobalCatalogMap
                        {
                            PossibleNames = possible,
                            Username = username
                        };
                        manager.InsertGCObject(obj);
                    }
                }

                if (DNSHostName == null)
                {
                    DNSHostName = cname;
                }

                if (obj.PossibleNames.Count == 0)
                {
                    //We didn't find the object in the GC at all. Default to computer domain
                    toReturn.Add(new SessionInfo
                    {
                        ComputerName = DNSHostName,
                        UserName = $"{username}@{ComputerDomain}",
                        Weight = 2
                    });
                }
                else if (obj.PossibleNames.Count == 1)
                {
                    //We found only one instance of the object
                    toReturn.Add(new SessionInfo
                    {
                        ComputerName = DNSHostName,
                        UserName = $"{username}@{obj.PossibleNames.First()}",
                        Weight = 1
                    });
                }
                else
                {
                    //Multiple possibilities. Add each one with a weight of 1 for the same domain as the computer
                    foreach (string p in obj.PossibleNames)
                    {
                        int weight;
                        if (p.ToUpper().Equals(ComputerDomain.ToUpper()))
                        {
                            weight = 1;
                        }
                        else
                        {
                            weight = 2;
                        }
                        toReturn.Add(new SessionInfo
                        {
                            ComputerName = DNSHostName,
                            UserName = $"{username}@{p}",
                            Weight = weight
                        });
                    }
                }
            }
            return toReturn;
        }

        #endregion

        #region pinvoke imports
        [DllImport("NetAPI32.dll", SetLastError = true)]
        static extern int NetSessionEnum(
            [MarshalAs(UnmanagedType.LPWStr)] string ServerName,
            [MarshalAs(UnmanagedType.LPWStr)] string UncClientName,
            [MarshalAs(UnmanagedType.LPWStr)] string UserName,
            int Level,
            out IntPtr bufptr,
            int prefmaxlen,
            out int entriesread,
            out int totalentries,
            ref IntPtr resume_handle);

        [StructLayout(LayoutKind.Sequential)]
        public struct SESSION_INFO_10
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string sesi10_cname;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string sesi10_username;
            public uint sesi10_time;
            public uint sesi10_idle_time;
        }

        public enum NERR
        {
            NERR_Success = 0,
            ERROR_MORE_DATA = 234,
            ERROR_NO_BROWSER_SERVERS_FOUND = 6118,
            ERROR_INVALID_LEVEL = 124,
            ERROR_ACCESS_DENIED = 5,
            ERROR_INVALID_PARAMETER = 87,
            ERROR_NOT_ENOUGH_MEMORY = 8,
            ERROR_NETWORK_BUSY = 54,
            ERROR_BAD_NETPATH = 53,
            ERROR_NO_NETWORK = 1222,
            ERROR_INVALID_HANDLE_STATE = 1609,
            ERROR_EXTENDED_ERROR = 1208,
            NERR_BASE = 2100,
            NERR_UnknownDevDir = (NERR_BASE + 16),
            NERR_DuplicateShare = (NERR_BASE + 18),
            NERR_BufTooSmall = (NERR_BASE + 23)
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WKSTA_USER_INFO_0
        {
            public string wkui0_username;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WKSTA_USER_INFO_1
        {
            public string wkui1_username;
            public string wkui1_logon_domain;
            public string wkui1_oth_domains;
            public string wkui1_logon_server;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int NetWkstaUserEnum(
           string servername,
           int level,
           out IntPtr bufptr,
           int prefmaxlen,
           out int entriesread,
           out int totalentries,
           ref int resume_handle);

        [DllImport("netapi32.dll")]
        static extern int NetApiBufferFree(
            IntPtr Buff);
        #endregion

        void Timer_Tick(object sender, System.Timers.ElapsedEventArgs args)
        {
            PrintStatus();
        }

        void PrintStatus()
        {
            int c = SessionEnumeration.total;
            int p = SessionEnumeration.count;
            int d = SessionEnumeration.dead;
            string progress = $"Session Enumeration for {SessionEnumeration.CurrentDomain} - {count}/{total} ({(float)(((dead + count) / total) * 100)}%) completed. ({count} hosts alive)";
            Console.WriteLine(progress);
        }
    }
}