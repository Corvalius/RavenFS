﻿using System;
using System.Linq;
using System.Threading.Tasks;

namespace RavenFS.Studio.Infrastructure
{
	public class ModelBase : NotifyPropertyChangedBase
	{
		private Task currentTask;
		private DateTime lastRefresh;
		protected TimeSpan RefreshRate { get; set; }

		protected ModelBase()
		{
			RefreshRate = TimeSpan.FromSeconds(5);
		}

		internal void ForceTimerTicked()
		{
			lastRefresh = DateTime.MinValue;
			TimerTicked();
		}

		internal void TimerTicked()
		{
			if (currentTask != null)
				return;

			lock (this)
			{
				if (currentTask != null)
					return;

				if (DateTime.Now - lastRefresh < GetRefreshRate())
					return;

				currentTask = TimerTickedAsync();

				if (currentTask == null)
					return;

				currentTask
					.Catch()
					.Finally(() =>
					{
						lastRefresh = DateTime.Now;
						currentTask = null;
					});
			}
		}

		private TimeSpan GetRefreshRate()
		{
			//if (Debugger.IsAttached)
			//    return RefreshRate.Add(TimeSpan.FromMinutes(5));
			return RefreshRate;
		}

		protected virtual Task TimerTickedAsync()
		{
			return null;
		}
	}
}
