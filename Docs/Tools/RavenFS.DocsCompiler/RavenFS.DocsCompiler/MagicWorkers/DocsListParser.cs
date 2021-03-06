﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RavenFS.DocsCompiler.Model;

namespace RavenFS.DocsCompiler.MagicWorkers
{
	public static class DocsListParser
	{
		//static readonly Regex DocsListLine = new Regex(@"\^[FILES-LIST(-RECURSIVE)?\]", RegexOptions.Compiled);
		static readonly Regex DocsListLine = new Regex(@"^([\w\-/\.]{2,})\t(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);

		public static IEnumerable<IDocumentationItem> Parse(string filePath)
		{
			if (!File.Exists(filePath))
				yield break;

			var contents = File.ReadAllText(filePath);

			var matches = DocsListLine.Matches(contents);
			foreach (Match match in matches)
			{
				var path = match.Groups[1].Value.Trim();
				if (path[0] == '\\' || path[0] == '/')
				{
					yield return new Folder
					             	{
					             		Title = match.Groups[2].Value.Trim(),
					             		Slug = path
					             	};
				}
				else
				{
					yield return new Document
					             	{
					             		Title = match.Groups[2].Value.Trim(),
					             		Slug = path,
					             	};
				}
				
			}
		}

		/*
		public static IEnumerable<MenuItem> ParseAll(string content)
		{
			var lines = content.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
			return lines.Select(ParseLine);
		}

		public static string ParseDocsList(this string content, string path, string slug)
		{
			return DocsListFinder.Replace(content, match => GenerateDocsListMenu(path, slug, !string.IsNullOrEmpty(match.Groups[1].Value)));
		}

		private static string GenerateDocsListMenu(string path, string slug, bool isRecursive)
		{
			var filePath = Path.Combine(Directory.GetParent(path).FullName, ".docslist");
			if (!File.Exists(filePath))
				return string.Empty;

			// TODO: consider isRecursive
			var menu = File.ReadAllText(filePath);
			var result = ParseAll(menu)
				.Select(item => string.Format("- [{0}]({1})", item.Title, slug + "/" + item.Slug));
			return string.Join(Environment.NewLine, result);
		}*/
	}
}
