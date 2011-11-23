﻿using RavenFS.Studio.Infrastructure;

namespace RavenFS.Studio.Commands
{
	public class NavigateToPrevPageCommand : Command
	{
		private readonly PagerModel pager;

		public NavigateToPrevPageCommand(PagerModel pager)
		{
			this.pager = pager;
		}

		public override void Execute(object parameter)
		{
			pager.NavigateToPrevPage();
		}

		public override bool CanExecute(object parameter)
		{
			return pager.HasPrevPage();
		}
	}
}