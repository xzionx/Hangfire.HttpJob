﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.HttpJob.Agent.Util;

namespace Hangfire.HttpJob.Agent
{
    public abstract class JobAgent
    {
        private ManualResetEvent _mainThread;

        /// <summary>
        ///     默认是非Hang
        /// </summary>
        internal volatile bool Hang = false;

        private JobStatus jobStatus = JobStatus.Default;

        /// <summary>
        ///     默认是单例
        /// </summary>
        internal volatile bool Singleton = true;

        /// <summary>
        ///     线程
        /// </summary>
        private Thread thd;


        /// <summary>
        ///     运行参数
        /// </summary>
        public string Param { get; private set; }

        internal string AgentClass { get; set; }

        /// <summary>
        ///     唯一标示
        /// </summary>
        internal string Guid { get; set; }

        /// <summary>
        ///     开始时间
        /// </summary>
        internal DateTime? StartTime { get; set; }

        /// <summary>
        ///     最后接
        /// </summary>
        internal DateTime? LastEndTime { get; set; }


        internal JobStatus JobStatus
        {
            get => jobStatus;
            set
            {
                jobStatus = value;
                if (value != JobStatus.Stoped)
                    return;
                LastEndTime = DateTime.Now;
            }
        }

        /// <summary>
        ///     多例job结束事件
        /// </summary>
        internal event EventHandler<TransitentJobDisposeArgs> TransitentJobDisposeEvent;

        public abstract Task OnStart(JobContext jobContext);
        public abstract void OnStop(JobContext jobContext);
        public abstract void OnException(Exception ex);


        internal void Run(string param, IHangfireConsole console, ConcurrentDictionary<string, string> headers)
        {
            if (JobStatus == JobStatus.Running) return;
            lock (this)
            {
                if (JobStatus == JobStatus.Running) return;
                _mainThread = new ManualResetEvent(false);
                Param = param;
                var jobContext = new JobContext
                {
                    Param = param,
                    Console = console,
                    Headers = headers
                };
                thd = new Thread(async () => { await start(jobContext); });
                thd.Start();
            }
        }

        internal void Stop(IHangfireConsole console, ConcurrentDictionary<string, string> headers)
        {
            if (JobStatus == JobStatus.Stoped || JobStatus == JobStatus.Stopping)
                return;

            lock (this)
            {
                try
                {
                    if (JobStatus == JobStatus.Stoped || JobStatus == JobStatus.Stopping)
                        return;

                    if (Hang)
                    {
                        WriteToDashBordConsole(console, $"【Job Hang OnStop】{AgentClass}");
                        _mainThread.Set();
                    }
                    else
                    {
                        WriteToDashBordConsole(console,
                            $"【{(Singleton ? "TransientJob" : "SingletonJob")} OnStop】{AgentClass}");
                    }

                    JobStatus = JobStatus.Stopping;
                    var jobContext = new JobContext
                    {
                        Param = Param,
                        Console = console,
                        Headers = headers
                    };
                    OnStop(jobContext);
                }
                catch (Exception e)
                {
                    WriteToDashBordConsole(console, Hang
                        ? $"【HangJob OnStop With Error】{AgentClass}，ex：{e.Message}"
                        : $"【{(Singleton ? "TransientJob" : "SingletonJob")} OnStop With Error】{AgentClass}，ex：{e.Message}");


                    e.Data.Add("Method", "OnStop");
                    e.Data.Add("AgentClass", AgentClass);
                    try
                    {
                        OnException(e);
                    }
                    catch (Exception)
                    {
                        //ignore
                    }
                }

                JobStatus = JobStatus.Stoped;
                DisposeJob();
            }
        }

        /// <summary>
        ///     job运行 在一个独立的线程中运行
        /// </summary>
        private async Task start(JobContext jobContext)
        {
            try
            {
                if (JobStatus == JobStatus.Running) return;
                StartTime = DateTime.Now;
                JobStatus = JobStatus.Running;

                WriteToDashBordConsole(jobContext.Console, Hang
                    ? $"【HangJob OnStart】{AgentClass}"
                    : $"【{(Singleton ? "TransientJob" : "SingletonJob")} OnStart】{AgentClass}");

                await OnStart(jobContext);
                if (Hang)
                {
                    WriteToDashBordConsole(jobContext.Console, $"【Job Hang Success】{AgentClass}");
                    _mainThread.WaitOne();
                }

                WriteToDashBordConsole(jobContext.Console, Hang
                    ? $"【HangJob End】{AgentClass}"
                    : $"【{(Singleton ? "TransientJob" : "SingletonJob")} End】{AgentClass}");
            }
            catch (Exception e)
            {
                WriteToDashBordConsole(jobContext.Console, Hang
                    ? $"【HangJob OnStart With Error】{AgentClass}，ex：{e.Message}"
                    : $"【{(Singleton ? "TransientJob" : "SingletonJob")} OnStart With Error】{AgentClass}，ex：{e.Message}");


                e.Data.Add("Method", "OnStart");
                e.Data.Add("AgentClass", AgentClass);
                try
                {
                    OnException(e);
                }
                catch (Exception)
                {
                    //ignore
                }
            }

            JobStatus = JobStatus.Stoped;
            DisposeJob();
        }

        private void WriteToDashBordConsole(IHangfireConsole console, string message)
        {
            try
            {
                console.WriteLine(message, ConsoleFontColor.DarkGreen);
            }
            catch (Exception)
            {
                //ignore
            }
        }

        /// <summary>
        ///     获取job的详情
        /// </summary>
        /// <returns></returns>
        internal string GetJobInfo()
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(AgentClass))
            {
                list.Add($"JobClass:【{AgentClass}】");
                list.Add($"JobType:【{(Singleton ? "Singleton" : "Transient")}】");
                list.Add($"JobStatus:【{JobStatus.ToString()}】");
                list.Add($"ExcuteParam:【{Param ?? string.Empty}】");
            }

            list.Add(
                $"StartTime:【{(StartTime == null ? "not start yet!" : StartTime.Value.ToString("yyyy-MM-dd HH:mm:ss"))}】");
            list.Add(
                $"LastEndTime:【{(LastEndTime == null ? "not end yet!" : LastEndTime.Value.ToString("yyyy-MM-dd HH:mm:ss"))}】");
            if (JobStatus == JobStatus.Running && StartTime != null)
                list.Add(
                    $"RunningTime:【{CodingUtil.ParseTimeSeconds((int) (DateTime.Now - StartTime.Value).TotalSeconds)}】");
            return string.Join("\r\n", list);
        }

        private void DisposeJob()
        {
            if (!Singleton) TransitentJobDisposeEvent?.Invoke(null, new TransitentJobDisposeArgs(AgentClass, Guid));
            _mainThread?.Dispose();
        }
    }
}