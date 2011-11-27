﻿using System;
using System.Diagnostics;
using System.Windows;
using System.Threading.Tasks;
using Raven.Client.Extensions;

namespace RavenFS.Studio.Infrastructure
{
	public static class InvocationExtensions
	{
		public static Action ViaCurrentDispatcher(this Action action)
		{
			var dispatcher = Deployment.Current.Dispatcher;
			return () =>
			{
				if (dispatcher.CheckAccess())
					action();
				dispatcher.InvokeAsync(action);
			};
		}

		public static Action<T> ViaCurrentDispatcher<T>(this Action<T> action)
		{
			var dispatcher = Deployment.Current.Dispatcher;
			return t =>
			{
				if (dispatcher.CheckAccess())
					action(t);
				dispatcher.InvokeAsync(() => action(t));
			};
		}

		public static Task ContinueOnSuccess<T>(this Task<T> parent, Action<T> action)
		{
			return parent.ContinueWith(task => action(task.Result));
		}

		public static Task<bool> ContinueWhenTrue(this Task<bool> parent, Action action)
		{
			return parent.ContinueWith(task =>
			{
				if (task.Result == false)
					return false;
				action();
				return true;
			});
		}

		public static Task<bool> ContinueWhenTrueInTheUIThread(this Task<bool> parent, Action action)
		{
			return parent.ContinueWhenTrue(() =>
			{
				if (Deployment.Current.Dispatcher.CheckAccess())
					action();
				Deployment.Current.Dispatcher.InvokeAsync(action)
					.Catch();
			});
		}

		public static Task<TResult> ContinueOnSuccess<T, TResult>(this Task<T> parent, Func<T, TResult> action)
		{
			return parent.ContinueWith(task => action(task.Result));
		}

		public static Task ContinueOnSuccess(this Task parent, Action action)
		{
			return parent.ContinueWith(task =>
			{
				if (task.IsFaulted)
					return task;

				return TaskEx.Run(action);
			}).Unwrap();
		}

		public static Task ContinueOnSuccessInTheUIThread(this Task parent, Action action)
		{
			return parent.ContinueOnSuccess(() =>
			{
				if (Deployment.Current.Dispatcher.CheckAccess())
					action();
				Deployment.Current.Dispatcher.InvokeAsync(action)
					.Catch();
			});
		}

		public static Task ContinueOnSuccess(this Task parent, Func<Task> action)
		{
			return parent.ContinueWith(task =>
			{
				if (task.IsFaulted)
					return task;

				return action();
			}).Unwrap();
		}

		public static Task Finally(this Task task, Action action)
		{
			task.ContinueWith(t => action());
			return task;
		}

		public static Task Catch(this Task parent)
		{
			return parent.Catch(e => { });
		}

		

		public static Task Catch(this Task parent, Action<AggregateException> action)
		{
			var stackTrace = new StackTrace();
			parent.ContinueWith(task =>
			{
				if (task.IsFaulted == false)
					return;

				var ex = task.Exception.ExtractSingleInnerException();
				Deployment.Current.Dispatcher.InvokeAsync(() => ErrorPresenter.Show(ex, stackTrace))
						.ContinueWith(_ => action(task.Exception));
			});

			return parent;
		}

		public static Task CatchIgnore<TException>(this Task parent) where TException : Exception
		{
			return parent.CatchIgnore<TException>(() => { });
		}

		public static Task CatchIgnore<TException>(this Task parent, Action action) where TException : Exception
		{
			parent.ContinueWith(task =>
			{
				if (task.IsFaulted == false)
					return;

				if (task.Exception.ExtractSingleInnerException() is TException == false)
					return;

				task.Exception.Handle(exception => true);
				action();
			});

			return parent;
		}
	}
}