﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Comet.Internal.Reload;
using Esp;
using Esp.Resources;
using Newtonsoft.Json.Linq;
using Reloadify.Internal;

namespace Reloadify {
	/// <summary>
	/// Preview server that process HTTP requests, evaluates them in the <see cref="VM"/>
	/// and preview them with the <see cref="Previewer"/>.
	/// </summary>
	public class Reload {

		public Action StartingReload { get; set; }
		public Action<(string ClassName, Type Type)> ReplaceType { get; set; }
		public Action FinishedReload { get; set; }
		IEvaluator eval;
		//TaskScheduler mainScheduler;
		bool isRunning;
		ICommunicatorClient client;

		public static Reload Instance { get; } = new Reload ();

		internal Reload ()
		{

		}

		public static Task<bool> Init (string ideIP = null, int idePort = Constants.DEFAULT_PORT)
		{
			if (Instance.isRunning) {
				return Task.FromResult (true);
			}
			return Instance.RunInternal (ideIP, idePort);
		}

		internal async Task<bool> RunInternal (string ideIP, int idePort)
		{
			if (isRunning) {
				return true;
			}
			var tcpClients = TcpCommunicatorClient.GetTcpCommunicatorsFromResource ();
			if (ideIP != null)
				tcpClients.Insert (0, new TcpCommunicatorClient { Ip = ideIP, Port = idePort });
			this.client = await DiscoveryService.Shared.FindConnection (tcpClients.ToArray ());

			client.DataReceived = HandleDataReceived;

			//mainScheduler = TaskScheduler.FromCurrentSynchronizationContext ();

			eval = new Evaluator ();
			isRunning = true;
			return true;
		}

		void ResetIDE ()
		{
			client.Send (new ResetMessage ());
		}

		string GetIdeIPFromResource ()
		{
			try {
				using (Stream stream = GetType ().Assembly.GetManifestResourceStream (Constants.IDE_IP_RESOURCE_NAME))
				using (StreamReader reader = new StreamReader (stream)) {
					return reader.ReadToEnd ().Split ('\n') [0].Trim ();
				}
			} catch (Exception ex) {
				Debug.WriteLine (ex);
				return null;
			}
		}

		async void HandleDataReceived (object e)
		{
			var container = e as JContainer;
			string type = (string)container ["Type"];

			if (type == typeof (EvalRequestMessage).Name) {
				await HandleEvalRequest (container.ToObject<EvalRequestMessage> ());
			} else if (type == typeof (ErrorMessage).Name) {
				//var errorMessage = container.ToObject<ErrorMessage>();
				//await uiToolkit.RunInUIThreadAsync(async () =>
				//{
				//	errorViewModel.SetError("Oh no! An exception!", errorMessage.Exception);
				//	await previewer.NotifyError(errorViewModel);
				//});
			}
		}

		async Task HandleEvalRequest (EvalRequestMessage request)
		{
			Debug.WriteLine ($"Handling request");
			EvalResponse evalResponse = new EvalResponse ();
			EvalResult result = new EvalResult ();
			try {
				var s = await eval.EvaluateCode (request, result);
				Debug.WriteLine ($"Evaluating: {s} - {result.FoundClasses.Count}");
				if (s && result.FoundClasses.Count > 0) {
					StartingReload?.Invoke ();
					foreach (var r in result.FoundClasses)
						ReplaceType?.Invoke (r);
					Debug.WriteLine ($"Triggering Reload");
					FinishedReload?.Invoke ();
				} else {
					foreach (var m in result.Messages)
						Debug.Write (m.Text);
				}
			} catch (Exception ex) {
				Debug.WriteLine (ex);
			}
		}

		internal static string Replace (string code, Dictionary<string, string> replaced)
		{

			string newCode = code;
			foreach (var pair in replaced) {
				if (pair.Key == pair.Value)
					continue;
				newCode = newCode.Replace ($" {pair.Key} ", $" {pair.Value} ");
				newCode = newCode.Replace ($" {pair.Key}(", $" {pair.Value}(");
				newCode = newCode.Replace ($" {pair.Key}:", $" {pair.Value}:");
			}
			return newCode;
		}
	}
}
