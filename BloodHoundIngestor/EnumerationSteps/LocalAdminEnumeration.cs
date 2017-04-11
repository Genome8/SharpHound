﻿using ExtensionMethods;
using SharpHound.BaseClasses;
using SharpHound.Exceptions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.DirectoryServices;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SharpHound
{
    class LocalAdminEnumeration
    {
        private Helpers helpers;
        private Options options;
        private DBManager db;
        private static int count;
        private static int dead;
        private static int total;
        private static string CurrentDomain;
        private ConcurrentDictionary<string, LocalAdminInfo> unresolved;

        public LocalAdminEnumeration()
        {
            helpers = Helpers.Instance;
            options = helpers.Options;
            db = helpers.DBManager;
            unresolved = new ConcurrentDictionary<string, LocalAdminInfo>();
        }

        public void StartEnumeration()
        {
            List<string> Domains = helpers.GetDomainList();
            Stopwatch watch = Stopwatch.StartNew();
            Stopwatch overwatch = Stopwatch.StartNew();
            foreach (string DomainName in Domains)
            {
                CurrentDomain = DomainName;

                if (options.Stealth)
                {
                    BlockingCollection<LocalAdminInfo> coll = new BlockingCollection<LocalAdminInfo>();
                    Task gpowriter = StartWriter(coll, options, Task.Factory);
                    EnumerateGPOAdmin(DomainName, coll);
                    gpowriter.Wait();
                    continue;
                }

                var computers =
                    db.GetComputers().Find(x => x.Domain.Equals(DomainName));

                total = computers.Count();
                BlockingCollection<Computer> input = new BlockingCollection<Computer>();
                BlockingCollection<LocalAdminInfo> output = new BlockingCollection<LocalAdminInfo>();

                LimitedConcurrencyLevelTaskScheduler scheduler = new LimitedConcurrencyLevelTaskScheduler(options.Threads);
                TaskFactory factory = new TaskFactory(scheduler);

                List<Task> taskhandles = new List<Task>();

                System.Timers.Timer t = new System.Timers.Timer();
                t.Elapsed += new System.Timers.ElapsedEventHandler(Timer_Tick);

                t.Interval = options.Interval;
                t.Enabled = true;

                Task writer = StartWriter(output, options, factory);
                for (int i = 0; i < options.Threads; i++)
                {
                    taskhandles.Add(StartConsumer(input, output, factory));
                }
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

        private void Timer_Tick(object sender, System.Timers.ElapsedEventArgs args)
        {
            PrintStatus();
        }

        private void PrintStatus()
        {
            int c = LocalAdminEnumeration.total;
            int p = LocalAdminEnumeration.count;
            int d = LocalAdminEnumeration.dead;
            string progress = $"Local Admin Enumeration for {LocalAdminEnumeration.CurrentDomain} - {count}/{total} ({(float)(((dead+count) / total) * 100)}%) completed. ({count} hosts alive)";
            Console.WriteLine(progress);
        }

        private Task StartWriter(BlockingCollection<LocalAdminInfo> output, Options _options, TaskFactory factory)
        {
            return factory.StartNew(() =>
            {
                if (_options.URI == null)
                {
                    using (StreamWriter writer = new StreamWriter(_options.GetFilePath("local_admins.csv")))
                    {
                        writer.WriteLine("ComputerName,AccountName,AccountType");
                        writer.AutoFlush = true;
                        foreach (LocalAdminInfo info in output.GetConsumingEnumerable())
                        {
                            writer.WriteLine(info.ToCSV());
                        }
                    }
                }
            });
        }

        public Task StartConsumer(BlockingCollection<Computer> input,BlockingCollection<LocalAdminInfo> output, TaskFactory factory)
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
                    }

                    List<LocalAdminInfo> results;

                    try
                    {
                        string sid = c.SID.Substring(0,c.SID.LastIndexOf("-"));
                        results = LocalGroupAPI(hostname, "Administrators", sid);
                    }catch (SystemDownException)
                    {
                        Interlocked.Increment(ref dead);
                        continue;
                    }
                    catch (APIFailedException)
                    {
                        try
                        {
                            results = LocalGroupWinNT(hostname, "Administrators");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                            Interlocked.Increment(ref dead);
                            continue;
                        }
                    }catch (Exception e)
                    {
                        Console.WriteLine("Exception in local admin enumeration");
                        Console.WriteLine(e);
                        continue;
                    }
                    Interlocked.Increment(ref count);
                    results.ForEach(output.Add);
                }
            });
        }

        #region Helpers
        private List<LocalAdminInfo> LocalGroupWinNT(string Target, string Group)
        {
            DirectoryEntry members = new DirectoryEntry($"WinNT://{Target}/{Group},group");
            List<LocalAdminInfo> users = new List<LocalAdminInfo>();
            string servername = Target.Split('.')[0].ToUpper();
            foreach (object member in (System.Collections.IEnumerable)members.Invoke("Members"))
            {
                using (DirectoryEntry m = new DirectoryEntry(member))
                {
                    byte[] sid = m.GetPropBytes("objectsid");
                    string sidstring = new SecurityIdentifier(sid, 0).ToString();
                    DBObject obj;
                    if (db.FindBySID(sidstring, out obj))
                    {
                        users.Add(new LocalAdminInfo
                        {
                            objectname = obj.BloodHoundDisplayName,
                            objecttype = obj.Type,
                            server = Target
                        });
                    }
                }
            }

            return users;
        }

        private List<LocalAdminInfo> LocalGroupAPI(string Target, string Group, string DomainSID)
        {
            int QueryLevel = 2;
            IntPtr PtrInfo = IntPtr.Zero;
            int EntriesRead = 0;
            int TotalRead = 0;
            IntPtr ResumeHandle = IntPtr.Zero;
            string MachineSID = "DUMMYSTRING";

            Type LMI2 = typeof(LOCALGROUP_MEMBERS_INFO_2);

            List<LocalAdminInfo> users = new List<LocalAdminInfo>();

            int val = NetLocalGroupGetMembers(Target, Group, QueryLevel, out PtrInfo, -1, out EntriesRead, out TotalRead, ResumeHandle);
            if (val == 1722)
            {
                throw new SystemDownException();
            }

            if (val != 0)
            {
                throw new APIFailedException();
            }

            if (EntriesRead > 0)
            {
                IntPtr iter = PtrInfo;
                List<LOCALGROUP_MEMBERS_INFO_2> list = new List<LOCALGROUP_MEMBERS_INFO_2>();
                for (int i = 0; i < EntriesRead; i++)
                {
                    LOCALGROUP_MEMBERS_INFO_2 data = (LOCALGROUP_MEMBERS_INFO_2)Marshal.PtrToStructure(iter, LMI2);
                    iter = (IntPtr)(iter.ToInt64() + Marshal.SizeOf(LMI2));
                    list.Add(data);
                }

                NetApiBufferFree(PtrInfo);

                foreach (LOCALGROUP_MEMBERS_INFO_2 data in list)
                {
                    string s;
                    ConvertSidToStringSid(data.lgrmi2_sid, out s);
                    if (s.EndsWith("-500") && !(s.StartsWith(DomainSID)))
                    {
                        MachineSID = s.Substring(0, s.LastIndexOf("-"));
                        break;
                    }
                }

                foreach (LOCALGROUP_MEMBERS_INFO_2 data in list)
                {
                    string ObjectName = data.lgrmi2_domainandname;
                    if (!ObjectName.Contains("\\"))
                    {
                        continue;
                    }

                    string[] sp = ObjectName.Split('\\');

                    if (sp[1].Equals(""))
                    {
                        continue;
                    }
                    if (ObjectName.StartsWith("NT Authority"))
                    {
                        continue;
                    }

                    string ObjectSID;
                    string ObjectType;
                    ConvertSidToStringSid(data.lgrmi2_sid, out ObjectSID);
                    if (ObjectSID.StartsWith(MachineSID))
                    {
                        
                        continue;
                    }

                    DBObject obj;
                    switch (data.lgrmi2_sidusage)
                    {
                        case (SID_NAME_USE.SidTypeUser):
                            db.FindUserBySID(ObjectSID, out obj);
                            ObjectType = "user";
                            break;
                        case (SID_NAME_USE.SidTypeComputer):
                            db.FindComputerBySID(ObjectSID, out obj);
                            ObjectType = "computer";
                            break;
                        case (SID_NAME_USE.SidTypeGroup):
                            db.FindGroupBySID(ObjectSID, out obj);
                            ObjectType = "group";
                            break;
                        default:
                            obj = null;
                            ObjectType = null;
                            break;
                    }
                    
                    if (obj == null)
                    {
                        DirectoryEntry entry = new DirectoryEntry($"LDAP://<SID={ObjectSID}>");
                        try
                        {
                            obj = entry.ConvertToDB();
                            if (obj == null)
                            {
                                continue;
                            }
                            db.InsertRecord(obj);
                        }
                        catch (COMException)
                        {
                            //We couldn't resolve the object, so fallback to manual determination
                            string domain = sp[0];
                            string username = sp[1];
                            Helpers.DomainMap.TryGetValue(domain, out domain);
                            if (ObjectType == "user" || ObjectType == "group")
                            {
                                obj = new DBObject
                                {
                                    BloodHoundDisplayName = $"{username}@{domain}".ToUpper(),
                                    Type = "user"
                                };
                            }
                            else
                            {
                                obj = new DBObject
                                {
                                    Type = "computer",
                                    BloodHoundDisplayName = $"{username.Substring(0, username.Length - 1)}.{domain}"
                                };
                            }
                        }
                    }

                    users.Add(new LocalAdminInfo
                    {
                        server = Target,
                        objectname = obj.BloodHoundDisplayName,
                        objecttype = obj.Type
                    });
                }
            }
            return users;
        }
        #endregion

        #region pinvoke-imports
        [DllImport("NetAPI32.dll", CharSet = CharSet.Unicode)]
        public extern static int NetLocalGroupGetMembers(
            [MarshalAs(UnmanagedType.LPWStr)] string servername,
            [MarshalAs(UnmanagedType.LPWStr)] string localgroupname,
            int level,
            out IntPtr bufptr,
            int prefmaxlen,
            out int entriesread,
            out int totalentries,
            IntPtr resume_handle);

        [DllImport("Netapi32.dll", SetLastError = true)]
        static extern int NetApiBufferFree(IntPtr Buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct LOCALGROUP_MEMBERS_INFO_2
        {
            public IntPtr lgrmi2_sid;
            public SID_NAME_USE lgrmi2_sidusage;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lgrmi2_domainandname;
        }

        public enum SID_NAME_USE
        {
            SidTypeUser = 1,
            SidTypeGroup,
            SidTypeDomain,
            SidTypeAlias,
            SidTypeWellKnownGroup,
            SidTypeDeletedAccount,
            SidTypeInvalid,
            SidTypeUnknown,
            SidTypeComputer
        }

        [DllImport("advapi32", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool ConvertSidToStringSid(IntPtr pSid, out string strSid);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr LocalFree(IntPtr hMem);
        #endregion

        private void EnumerateGPOAdmin(string DomainName, BlockingCollection<LocalAdminInfo> output)
        {
            string targetsid = "S-1-5-32-544__Members";

            Console.WriteLine("Starting GPO Correlation");

            DirectorySearcher gposearcher = helpers.GetDomainSearcher(DomainName);
            gposearcher.Filter = "(&(objectCategory=groupPolicyContainer)(name=*)(gpcfilesyspath=*))";
            gposearcher.PropertiesToLoad.AddRange(new string[] { "displayname", "name", "gpcfilesyspath" });

            ConcurrentQueue<string> INIResults = new ConcurrentQueue<string>();

            Parallel.ForEach(gposearcher.FindAll().Cast<SearchResult>().ToArray(), (result) =>
            {
                string display = result.GetProp("displayname");
                string name = result.GetProp("name");
                string path  = result.GetProp("gpcfilesyspath");

                if (display == null || name == null || path == null)
                {
                    return;
                }

                string template = String.Format("{0}\\{1}", path, "MACHINE\\Microsoft\\Windows NT\\SecEdit\\GptTmpl.inf");
                
                using (StreamReader sr = new StreamReader(template))
                {
                    string line = String.Empty;
                    string currsection = String.Empty;
                    while ((line = sr.ReadLine()) != null)
                    {
                        Match section = Regex.Match(line, @"^\[(.+)\]");
                        if (section.Success)
                        {
                            currsection = section.Captures[0].Value.Trim();
                        }
                        
                        if (!currsection.Equals("[Group Membership]"))
                        {
                            continue;
                        }

                        Match key = Regex.Match(line, @"(.+?)\s*=(.*)");
                        if (key.Success)
                        {
                            string n = key.Groups[1].Value;
                            string v = key.Groups[2].Value;
                            if (n.Contains(targetsid))
                            {
                                v = v.Trim();
                                List<String> members = v.Split(',').ToList();
                                List<DBObject> resolved = new List<DBObject>();
                                for (int i = 0; i < members.Count; i++)
                                {
                                    string m = members[i];
                                    m = m.Trim('*');

                                    string sid;
                                    if (!m.StartsWith("S-1-"))
                                    {
                                        try
                                        {
                                            sid = new System.Security.Principal.NTAccount(DomainName, m).Translate(typeof(System.Security.Principal.SecurityIdentifier)).Value;
                                        }
                                        catch
                                        {
                                            sid = null;
                                        }
                                    }
                                    else
                                    {
                                        sid = m;
                                    }
                                    if (sid == null)
                                    {
                                        continue;
                                    }

                                    string user = null;

                                    DBObject obj;
                                    if (db.FindBySID(sid,out obj))
                                    {
                                        user = obj.BloodHoundDisplayName;
                                    }
                                    else
                                    {
                                        try
                                        {
                                            DirectoryEntry entry = new DirectoryEntry($"LDAP://<SID={sid}");
                                            obj = entry.ConvertToDB();
                                            db.InsertRecord(obj);
                                        }
                                        catch
                                        {
                                            obj = null;
                                        }
                                    }

                                    if (obj != null)
                                    {
                                        resolved.Add(obj);
                                    }
                                }
                                DirectorySearcher OUSearch = helpers.GetDomainSearcher(DomainName);
                                
                                OUSearch.Filter = $"(&(objectCategory=organizationalUnit)(name=*)(gplink=*{name}*))";
                                foreach (SearchResult r in OUSearch.FindAll())
                                {
                                    DirectorySearcher compsearcher = helpers.GetDomainSearcher(DomainName, ADSPath: r.GetProp("adspath"));
                                    foreach (SearchResult ra in compsearcher.FindAll())
                                    {
                                        string sat = ra.GetProp("samaccounttype");
                                        if (sat == null)
                                        {
                                            continue;
                                        }

                                        DBObject resultdb = ra.ConvertToDB();

                                        if (sat.Equals("805306369"))
                                        {
                                            foreach (DBObject obj in resolved)
                                            {
                                                output.Add(new LocalAdminInfo
                                                {
                                                    objectname = obj.BloodHoundDisplayName,
                                                    objecttype = obj.Type,
                                                    server = resultdb.BloodHoundDisplayName
                                                });
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            });

            output.CompleteAdding();

            Console.WriteLine("Done GPO Correlation");
        }        
    }
}