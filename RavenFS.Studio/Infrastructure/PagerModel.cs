﻿using System;
using System.Windows.Input;
using RavenFS.Studio.Commands;

namespace RavenFS.Studio.Infrastructure
{
	public class PagerModel : Model
	{
		public event EventHandler Navigated;

		public PagerModel()
		{
			PageSize = 2;
			SetTotalResults(new Observable<long>());
		}

		public void SetTotalResults(Observable<long> observable)
		{
			TotalResults = observable;
			TotalResults.PropertyChanged += (sender, args) =>
			{
				OnPropertyChanged("TotalPages");
				OnPropertyChanged("HasNextPage");
			};
		}

		public int PageSize { get; set; }

		public int CurrentPage
		{
			get { return Skip / PageSize + 1; }
		}

		public Observable<long> TotalResults { get; private set; }
		public long TotalPages
		{
			get { return TotalResults.Value / PageSize + 1; }
		}

		private ushort? skip;
		public ushort Skip
		{
			get
			{
				if (skip == null)
				{
					SetSkip(new UrlParser(UrlUtil.Url));
				}
				return skip.Value;
			}
			set
			{
				skip = value;
				OnPropertyChanged("CurrentPage");
				OnPropertyChanged("HasPrevPage");
			}
		}

		public void SetSkip(UrlParser urlParser)
		{
			ushort s;
			ushort.TryParse(urlParser.GetQueryParam("skip"), out s);
			Skip = s;
		}

		public bool HasNextPage()
		{
			return CurrentPage < TotalPages;
		}

		public bool HasPrevPage()
		{
			return Skip > 0;
		}

		public void NavigateToNextPage()
		{
			NavigateToPage(1);
		}

		public void NavigateToPrevPage()
		{
			NavigateToPage(-1);
		}

		private void NavigateToPage(int pageOffset)
		{
			var skip1 = Skip + pageOffset * PageSize;
			Skip = (ushort)skip1;
			var urlParser = new UrlParser(UrlUtil.Url);
			urlParser.SetQueryParam("skip", Skip);
			UrlUtil.Navigate(urlParser.BuildUrl());

			if (Navigated != null)
				Navigated(this, EventArgs.Empty);
		}

		public ICommand NextPage
		{
			get { return new NavigateToNextPageCommand(this); }
		}

		public ICommand PrevPage
		{
			get { return new NavigateToPrevPageCommand(this); }
		}
	}
}