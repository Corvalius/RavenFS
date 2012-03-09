﻿using System;
using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace RavenFS.Studio.Infrastructure
{
    /// <summary>
    /// A list which can support a huge virtual item count
    /// by assuming that many items are never accessed. Space for items is allocated
    /// in pages.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class SparseList<T>
    {
        private readonly int _pageSize;
        private readonly PageList _allocatedPages;
        private Page _currentPage;

        public SparseList(int pageSize)
        {
            _pageSize = pageSize;
            _allocatedPages = new PageList(_pageSize);
        }

        /// <remarks>This method is optimised for sequential access. I.e. it performs
        /// best when getting and setting indicies in the same locality</remarks>
        public T this[int index]
        {
            get
            {
                var pageAndSubIndex = GetPageAndSubIndex(index);

                if (_currentPage == null || _currentPage.PageIndex != pageAndSubIndex.PageIndex)
                {
                    _currentPage = _allocatedPages.GetOrCreatePage(pageAndSubIndex.PageIndex);
                }

                return _currentPage[pageAndSubIndex.SubIndex];
            }
            set
            {
                var pageAndSubIndex = GetPageAndSubIndex(index);

                if (_currentPage == null || _currentPage.PageIndex != pageAndSubIndex.PageIndex)
                {
                    _currentPage = _allocatedPages.GetOrCreatePage(pageAndSubIndex.PageIndex);
                }

                _currentPage[pageAndSubIndex.SubIndex] = value;
            }
        }

        private PageAndSubIndex GetPageAndSubIndex(int itemIndex)
        {
            return new PageAndSubIndex(itemIndex / _pageSize, itemIndex % _pageSize);
        }

        private struct PageAndSubIndex
        {
            private readonly int _pageIndex;
            private readonly int _subIndex;

            public PageAndSubIndex(int pageIndex, int subIndex)
            {
                _pageIndex = pageIndex;
                _subIndex = subIndex;
            }

            public int PageIndex
            {
                get { return _pageIndex; }
            }

            public int SubIndex
            {
                get { return _subIndex; }
            }
        }

        private class Page
        {
            private readonly int _pageIndex;
            private readonly T[] _items;

            public Page(int pageIndex, int pageSize)
            {
                _pageIndex = pageIndex;
                _items = new T[pageSize];
            }

            public int PageIndex
            {
                get { return _pageIndex; }
            }

            public T this[int index]
            {
                get { return _items[index]; }
                set { _items[index] = value; }
            }
        }

        private class PageList : KeyedCollection<int, Page>
        {
            private readonly int _pageSize;

            public PageList(int pageSize)
            {
                _pageSize = pageSize;
            }

            protected override int GetKeyForItem(Page item)
            {
                return item.PageIndex;
            }

            public Page GetOrCreatePage(int pageIndex)
            {
                if (!Contains(pageIndex))
                {
                    Add(new Page(pageIndex, _pageSize));
                }

                return this[pageIndex];
            }
        }
    }
}