﻿using NetworkSocket.Core;
using NetworkSocket.Exceptions;
using NetworkSocket.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkSocket.Fast
{
    /// <summary>
    /// 表示Fast协议的Api服务基类
    /// </summary>
    public abstract class FastApiService : FastFilterAttribute, IFastApiService
    {
        /// <summary>
        /// 逻辑调用上下文
        /// </summary>
        private readonly LogicalContext<ActionContext> logicalContext = new LogicalContext<ActionContext>();

        /// <summary>
        /// 获取当前Api行为上下文
        /// </summary>
        protected ActionContext CurrentContext
        {
            get
            {
                return this.logicalContext.GetValue();
            }
        }

        /// <summary>
        /// 获取关联的服务器实例
        /// </summary>
        private FastMiddleware Middleware
        {
            get
            {
                return CurrentContext.Session.Middleware;
            }
        }

        /// <summary>
        /// 执行Api行为
        /// </summary>   
        /// <param name="actionContext">上下文</param>      
        /// <returns></returns>
        async Task IFastApiService.ExecuteAsync(ActionContext actionContext)
        {
            var filters = Enumerable.Empty<IFilter>();
            try
            {
                this.logicalContext.SetValue(actionContext);
                filters = this.Middleware.FilterAttributeProvider.GetActionFilters(actionContext.Action);
                await this.ExecuteActionAsync(actionContext, filters);
            }
            catch (Exception ex)
            {
                this.ProcessExecutingException(actionContext, filters, ex);
            }
            finally
            {
                this.logicalContext.FreeValue();
            }
        }

        /// <summary>
        /// 处理Api行为执行过程中产生的异常
        /// </summary>
        /// <param name="actionContext">上下文</param>
        /// <param name="filters">过滤器</param>
        /// <param name="exception">异常项</param>      
        private void ProcessExecutingException(ActionContext actionContext, IEnumerable<IFilter> filters, Exception exception)
        {
            var exceptionContext = new ExceptionContext(actionContext, new ApiExecuteException(exception));
            Common.SendRemoteException(actionContext.Session, exceptionContext);
            this.ExecAllExceptionFilters(filters, exceptionContext);
        }

        /// <summary>
        /// 调用自身实现的Api行为            
        /// </summary>       
        /// <param name="actionContext">上下文</param>       
        /// <param name="filters">过滤器</param>
        /// <returns></returns>
        private async Task ExecuteActionAsync(ActionContext actionContext, IEnumerable<IFilter> filters)
        {
            Common.GetAndUpdateParameterValues(this.Middleware.Serializer, actionContext);
            this.ExecFiltersBeforeAction(filters, actionContext);

            if (actionContext.Result == null)
            {
                await this.ExecutingActionAsync(actionContext, filters);
            }
            else
            {
                var exceptionContext = new ExceptionContext(actionContext, actionContext.Result);
                Common.SendRemoteException(actionContext.Session, exceptionContext);
            }
        }

        /// <summary>
        /// 异步执行Api
        /// </summary>
        /// <param name="actionContext">上下文</param>
        /// <param name="filters">过滤器</param>
        /// <returns></returns>
        private async Task ExecutingActionAsync(ActionContext actionContext, IEnumerable<IFilter> filters)
        {
            try
            {
                var paramters = actionContext.Action.ParameterValues;
                var result = await actionContext.Action.ExecuteAsync(this, paramters);

                this.ExecFiltersAfterAction(filters, actionContext);
                if (actionContext.Result != null)
                {
                    var exceptionContext = new ExceptionContext(actionContext, actionContext.Result);
                    Common.SendRemoteException(actionContext.Session, exceptionContext);
                }
                else if (actionContext.Action.IsVoidReturn == false && actionContext.Session.IsConnected)  // 返回数据
                {
                    actionContext.Packet.Body = this.Middleware.Serializer.Serialize(result);
                    actionContext.Session.UnWrap().Send(actionContext.Packet.ToArraySegment());
                }
            }
            finally
            {
                this.logicalContext.FreeValue();
            }
        }

        /// <summary>
        /// 在Api行为前 执行过滤器
        /// </summary>       
        /// <param name="filters">Api行为过滤器</param>
        /// <param name="actionContext">上下文</param>   
        private void ExecFiltersBeforeAction(IEnumerable<IFilter> filters, ActionContext actionContext)
        {
            var totalFilters = this.GetTotalFilters(filters);
            foreach (var filter in totalFilters)
            {
                filter.OnExecuting(actionContext);
                if (actionContext.Result != null) break;
            }
        }

        /// <summary>
        /// 在Api行为后执行过滤器
        /// </summary>       
        /// <param name="filters">Api行为过滤器</param>
        /// <param name="actionContext">上下文</param>       
        private void ExecFiltersAfterAction(IEnumerable<IFilter> filters, ActionContext actionContext)
        {
            var totalFilters = this.GetTotalFilters(filters);
            foreach (var filter in totalFilters)
            {
                filter.OnExecuted(actionContext);
                if (actionContext.Result != null) break;
            }
        }

        /// <summary>
        /// 执行异常过滤器
        /// </summary>       
        /// <param name="filters">Api行为过滤器</param>
        /// <param name="exceptionContext">上下文</param>       
        private void ExecAllExceptionFilters(IEnumerable<IFilter> filters, ExceptionContext exceptionContext)
        {
            var totalFilters = this.GetTotalFilters(filters);
            foreach (var filter in totalFilters)
            {
                filter.OnException(exceptionContext);
                if (exceptionContext.ExceptionHandled == true) break;
            }

            if (exceptionContext.ExceptionHandled == false)
            {
                throw exceptionContext.Exception;
            }
        }


        /// <summary>
        /// 获取全部的过滤器
        /// </summary>
        /// <param name="filters">行为过滤器</param>
        /// <returns></returns>
        private IEnumerable<IFilter> GetTotalFilters(IEnumerable<IFilter> filters)
        {
            return this.Middleware
                .GlobalFilters
                .Cast<IFilter>()
                .Concat(new[] { this })
                .Concat(filters);
        }

        #region IDisponse
        /// <summary>
        /// 获取对象是否已释放
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// 关闭和释放所有相关资源
        /// </summary>
        public void Dispose()
        {
            if (this.IsDisposed == false)
            {
                this.Dispose(true);
                GC.SuppressFinalize(this);
            }
            this.IsDisposed = true;
        }

        /// <summary>
        /// 析构函数
        /// </summary>
        ~FastApiService()
        {
            this.Dispose(false);
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        /// <param name="disposing">是否也释放托管资源</param>
        protected virtual void Dispose(bool disposing)
        {
        }
        #endregion
    }
}
