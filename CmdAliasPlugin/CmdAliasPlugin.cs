﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Text.RegularExpressions;
using TerrariaApi.Server;
using Wolfje.Plugins.SEconomy;
using System.Threading.Tasks;



namespace Wolfje.Plugins.SEconomy.CmdAliasModule {

	/// <summary>
	/// Provides command aliases that can cost money to execute in SEconomy.
	/// </summary>
	[ApiVersion(1, 16)]
	public class CmdAliasPlugin : TerrariaPlugin {

		static Configuration Configuration { get; set; }
		static readonly object __rndLock = new object();
		static readonly Random randomGenerator = new Random();

		public static event EventHandler<AliasExecutedEventArgs> AliasExecuted;

		#region "API stub"

		public CmdAliasPlugin(Terraria.Main game)
			: base(game)
		{
			//we're absolute last in the plugin order.
			Order = int.MaxValue - 1;
		}

		public override string Author
		{
			get
			{
				return "Wolfje";
			}
		}

		public override string Description
		{
			get
			{
				return "Provides a list of customized command aliases that cost money in SEconomy.";
			}
		}

		public override string Name
		{
			get
			{
				return "CmdAlias";
			}
		}

		public override Version Version
		{
			get
			{
				return Assembly.GetExecutingAssembly().GetName().Version;
			}
		}

		#endregion

		static readonly Regex parameterRegex = new Regex(@"\$(\d)(-(\d)?)?");
		static readonly Regex randomRegex = new Regex(@"\$random\((\d*),(\d*)\)", RegexOptions.IgnoreCase);
		static readonly Regex runasFunctionRegex = new Regex(@"(\$runas\((.*?),(.*?)\)$)", RegexOptions.IgnoreCase);
		static readonly Regex msgRegex = new Regex(@"(\$msg\((.*?),(.*?)\)$)", RegexOptions.IgnoreCase);



		/// <summary>
		/// Format for this dictionary:
		/// Key: KVP with User's character name, and the command they ran>
		/// Value: UTC datetime when they last ran that command
		/// </summary>
		public static readonly Dictionary<KeyValuePair<string, AliasCommand>, DateTime> CooldownList = new Dictionary<KeyValuePair<string, AliasCommand>, DateTime>();

		public override void Initialize()
		{
			TShockAPI.Commands.ChatCommands.Add(new TShockAPI.Command("aliascmd", ChatCommand_GeneralCommand, "aliascmd") { AllowServer = true });
			Configuration = Configuration.LoadConfigurationFromFile("tshock" + System.IO.Path.DirectorySeparatorChar + "SEconomy" + System.IO.Path.DirectorySeparatorChar + "AliasCmd.config.json");
			ParseCommands();
			AliasExecuted += CmdAliasPlugin_AliasExecuted;
		}

		private void ChatCommand_GeneralCommand(TShockAPI.CommandArgs args)
		{

			if (args.Parameters.Count >= 1 && args.Parameters[0].Equals("reload", StringComparison.CurrentCultureIgnoreCase) && args.Player.Group.HasPermission("aliascmd.reloadconfig")) {
				args.Player.SendInfoMessage("aliascmd: Reloading configuration file.");

				ReloadConfigAfterDelayAsync(1).ContinueWith((task) => {
					if (task.IsFaulted) {
						args.Player.SendErrorMessage("aliascmd: reload failed.  You need to check the server console to find out what went wrong.");
						TShockAPI.Log.ConsoleError(task.Exception.ToString());
					} else {
						args.Player.SendInfoMessage("aliascmd: reload successful.");
					}
				});
			} else {
				args.Player.SendErrorMessageFormat("aliascmd: usage: /aliascmd reload: reloads the AliasCmd configuration file.");
			}
		}

		/// <summary>
		/// Asynchronously reparses the AliasCmd configuration file after the specified period.
		/// </summary>
		Task ReloadConfigAfterDelayAsync(int DelaySeconds)
		{
			return Task.Factory.StartNew(() => {
				System.Threading.Thread.Sleep(DelaySeconds * 1000);

				TShockAPI.Log.ConsoleInfo("AliasCmd: reloading config.");

				try {
					Configuration reloadedConfig = Configuration.LoadConfigurationFromFile("tshock" + System.IO.Path.DirectorySeparatorChar + "SEconomy" + System.IO.Path.DirectorySeparatorChar + "AliasCmd.config.json");
					Configuration = reloadedConfig;

					ParseCommands();

				} catch (Exception ex) {
					TShockAPI.Log.ConsoleError("aliascmd: Your new config could not be loaded, fix any problems and save the file.  Your old configuration is in effect until this is fixed. \r\n\r\n" + ex.ToString());
					throw;
				}

				TShockAPI.Log.ConsoleInfo("AliasCmd: config reload done.");

			});

		}

		void CmdAliasPlugin_AliasExecuted(object sender, AliasExecutedEventArgs e)
		{
			//Get the corresponding alias in the config that matches what the user typed.
			foreach (AliasCommand alias in Configuration.CommandAliases.Where(i => i.CommandAlias == e.CommandIdentifier)) {
				TimeSpan timeSinceLastUsedCommand = TimeSpan.MaxValue;
				Money commandCost = 0;
				Economy.EconomyPlayer ePlayer = null;

				if (alias == null) {
					continue;
				}

				//cooldown key is a pair of the user's character name, and the command they have called.
				//cooldown value is a DateTime they last used the command.
				KeyValuePair<string, AliasCommand> cooldownReference = new KeyValuePair<string, AliasCommand>(e.CommandArgs.Player.Name, alias);
				if (CooldownList.ContainsKey(cooldownReference)) {
					//UTC time so we don't get any daylight saving shit cuntery
					timeSinceLastUsedCommand = DateTime.UtcNow.Subtract(CooldownList[cooldownReference]);
				}

				//has the time elapsed greater than the cooldown period?
				if (timeSinceLastUsedCommand.TotalSeconds >= alias.CooldownSeconds || e.CommandArgs.Player.Group.HasPermission("aliascmd.bypasscooldown")) {
					e.CommandArgs.Player.SendErrorMessageFormat("{0}: You need to wait {1:0} more seconds to be able to use that.", alias.CommandAlias, (alias.CooldownSeconds - timeSinceLastUsedCommand.TotalSeconds));
					return;
				}

				ePlayer = SEconomyPlugin.GetEconomyPlayerSafe(e.CommandArgs.Player.Index);
				if (ePlayer == null || ePlayer.BankAccount == null) {
					e.CommandArgs.Player.SendErrorMessageFormat("This command costs money and you don't have a bank account.  Please log in first.");
					return;
				}

				if (string.IsNullOrEmpty(alias.Cost) == true || e.CommandArgs.Player.Group.HasPermission("aliascmd.bypasscost") == true) {
					DoCommands(alias, e.CommandArgs.Player, e.CommandArgs.Parameters);
					return;
				}

				if (Money.TryParse(alias.Cost, out commandCost) == false) {
					e.CommandArgs.Player.SendErrorMessageFormat("This command has an invalid cost, please seek your administrator.");
					return;
				}

				if (ePlayer.BankAccount.IsAccountEnabled == false) {
					e.CommandArgs.Player.SendErrorMessageFormat("This command costs money and your account is disabled.");
					return;
				}

				if (ePlayer.BankAccount.Balance < commandCost) {
					Money difference = commandCost - ePlayer.BankAccount.Balance;
					e.CommandArgs.Player.SendErrorMessageFormat("This command costs {0}. You need {1} more to be able to use this.", commandCost.ToLongString(), difference.ToLongString());
				}

				try {
					//Take money off the player, and indicate that this is a payment for something tangible.
					Journal.BankTransferEventArgs trans = ePlayer.BankAccount.TransferTo(SEconomyPlugin.WorldAccount, commandCost, Journal.BankAccountTransferOptions.AnnounceToSender | Journal.BankAccountTransferOptions.IsPayment, "", string.Format("AC: {0} cmd {1}", ePlayer.TSPlayer.Name, alias.CommandAlias));
					if (trans.TransferSucceeded) {
						DoCommands(alias, ePlayer.TSPlayer, e.CommandArgs.Parameters);
					} else {
						e.CommandArgs.Player.SendErrorMessageFormat("Your payment failed.");
						return;
					}
				} catch (Exception ex) {
					e.CommandArgs.Player.SendErrorMessageFormat("An error occured in the alias.");
					TShockAPI.Log.ConsoleError("aliascmd error: {0} tried to execute alias {1} which failed with error {2}: {3}", e.CommandArgs.Player.Name, e.CommandIdentifier, ex.Message, ex.ToString());
					return;
				}

				//populate the cooldown list.  This dictionary does not go away when people leave so they can't
				//reset cooldowns by simply logging out or disconnecting.  They can reset it however by logging into 
				//a different account.
				if (CooldownList.ContainsKey(cooldownReference)) {
					CooldownList[cooldownReference] = DateTime.UtcNow;
				} else {
					CooldownList.Add(cooldownReference, DateTime.UtcNow);
				}

			}
		}

		public void ParseCommands()
		{
			lock (TShockAPI.Commands.ChatCommands) {
				TShockAPI.Commands.ChatCommands.RemoveAll(i => i.Names.Count(x => x.StartsWith("cmdalias.")) > 0);

				foreach (AliasCommand aliasCmd in Configuration.CommandAliases) {
					//The command delegate points to the same function for all aliases, which will generically handle all of them.
					TShockAPI.Command tsCommand = TShockAPI.Commands.ChatCommands.SingleOrDefault(i => i.Name == aliasCmd.CommandAlias);

					TShockAPI.Command newCommand = new TShockAPI.Command(aliasCmd.Permissions, ChatCommand_AliasExecuted, new string[] { aliasCmd.CommandAlias, "cmdalias." + aliasCmd.CommandAlias }) { AllowServer = true };
					TShockAPI.Commands.ChatCommands.Add(newCommand);
				}
			}
		}


		/// <summary>
		/// Mangles the command to execute with the supplied parameters according to the parameter rules.
		/// </summary>
		/// <param name="parameters">
		///      * Parameter format:
		///      * $1 $2 $3 $4: Takes the individual parameter number from the typed alias and puts it into the commands to execute
		///      * $1-: Takes everything from the indiviual parameter to the end of the line
		///      * $1-3: Take all parameters ranging from the lowest to the highest.
		/// </param>
		static void ReplaceParameterMarkers(IList<string> parameters, ref string CommandToExecute)
		{
			if (parameterRegex.IsMatch(CommandToExecute)) {

				/* Parameter format:
				 * 
				 * $1 $2 $3 $4: Takes the individual parameter number from the typed alias and puts it into the commands to execute
				 * $1-: Takes everything from the indiviual parameter to the end of the line
				 * $1-3: Take all parameters ranging from the lowest to the highest.
				 */
				foreach (Match match in parameterRegex.Matches(CommandToExecute)) {
					int parameterFrom = !string.IsNullOrEmpty(match.Groups[1].Value) ? int.Parse(match.Groups[1].Value) : 0;
					int parameterTo = !string.IsNullOrEmpty(match.Groups[3].Value) ? int.Parse(match.Groups[3].Value) : 0;
					bool takeMoreThanOne = !string.IsNullOrEmpty(match.Groups[2].Value);
					StringBuilder sb = new StringBuilder();


					//take n
					if (!takeMoreThanOne && parameterFrom > 0) {
						if (parameterFrom <= parameters.Count) {
							sb.Append(parameters[parameterFrom - 1]);
						} else {
							//If the match is put there but no parameter was input, then replace it with nothing.
							sb.Append("");
						}

						//take from n to x
					} else if (takeMoreThanOne && parameterTo > parameterFrom) {
						for (int i = parameterFrom; i <= parameterTo; ++i) {
							if (parameters.Count >= i) {
								sb.Append(" " + parameters[i - 1]);
							}
						}

						//take from n to infinite.
					} else if (takeMoreThanOne && parameterTo == 0) {
						for (int i = parameterFrom; i <= parameters.Count; ++i) {
							sb.Append(" " + parameters[i - 1]);
						}
						//do fuck all lelz
					} else {
						sb.Append("");
					}

					//replace the match expression with the replacement.Oh
					CommandToExecute = CommandToExecute.Replace(match.ToString(), sb.ToString());
				}
			}
		}

		/// <summary>
		/// Executes the AliasCommand.  Will either forward the command to the tshock handler or do something else
		/// </summary>
		static void DoCommands(AliasCommand alias, TShockAPI.TSPlayer player, List<string> parameters)
		{

			//loop through each alias and do the commands.
			foreach (string commandToExecute in alias.CommandsToExecute) {
				//todo: parse paramaters and dynamics
				string mangledString = commandToExecute;

				//specifies whether the command to run should be executed as a command, or ignored.
				//useful for functions like $msg that does other shit
				bool executeCommand = true;

				//replace parameter markers with actual parameter values
				ReplaceParameterMarkers(parameters, ref mangledString);

				mangledString = mangledString.Replace("$calleraccount", player.UserAccountName);
				mangledString = mangledString.Replace("$callername", player.Name);

				//$random(x,y) support.  Returns a random number between x and y
				if (randomRegex.IsMatch(mangledString)) {
					foreach (Match match in randomRegex.Matches(mangledString)) {
						int randomFrom = 0;
						int randomTo = 0;

						if (!string.IsNullOrEmpty(match.Groups[2].Value) && int.TryParse(match.Groups[2].Value, out randomTo)
							&& !string.IsNullOrEmpty(match.Groups[1].Value) && int.TryParse(match.Groups[1].Value, out randomFrom)) {

							// this is a critical section
							// Random class is seeded from the system clock if you construct one without a seed.
							// therefore, calls to Next() at exactly the same point in time is likely to produce the same number.
							lock (__rndLock) {
								mangledString = mangledString.Replace(match.ToString(), randomGenerator.Next(randomFrom, randomTo).ToString());
							}
						} else {
							TShockAPI.Log.ConsoleError(match.ToString() + " has some stupid shit in it, have a look at your AliasCmd config file.");
							mangledString = mangledString.Replace(match.ToString(), "");
						}
					}
				}

				// $runas(u,cmd) support.  Run command as user
				if (runasFunctionRegex.IsMatch(mangledString)) {

					foreach (Match match in runasFunctionRegex.Matches(mangledString)) {
						string impersonatedName = match.Groups[2].Value;
						Economy.EconomyPlayer impersonatedPlayer = SEconomyPlugin.GetEconomyPlayerSafe(impersonatedName);

						if (impersonatedPlayer != null) {
							string commandToRun = match.Groups[3].Value;
							player = impersonatedPlayer.TSPlayer;

							mangledString = commandToRun.Trim();
						}
					}
				}

				// $msg(u,msg) support.  Sends the user a non-chat informational message
				if (msgRegex.IsMatch(mangledString)) {

					foreach (Match match in msgRegex.Matches(mangledString)) {
						string msgTarget = match.Groups[2].Value.Trim();
						string message = match.Groups[3].Value.Trim();
						Economy.EconomyPlayer destinationPlayer = SEconomyPlugin.GetEconomyPlayerSafe(msgTarget);

						if (destinationPlayer != null) {
							//custom command, skip forwarding of the command to the tshock executer
							executeCommand = false;

							destinationPlayer.TSPlayer.SendInfoMessage(message);
						}
					}
				}


				//and send the command to tshock to do.
				try {
					//prevent an infinite loop for a subcommand calling the alias again causing a commandloop
					string command = mangledString.Split(' ')[0].Substring(1);
					if (!command.Equals(alias.CommandAlias, StringComparison.CurrentCultureIgnoreCase)) {
						if (executeCommand) {
							HandleCommandWithoutPermissions(player, mangledString);
						}
					} else {
						TShockAPI.Log.ConsoleError(string.Format("cmdalias {0}: calling yourself in an alias will cause an infinite loop. Ignoring.", alias.CommandAlias));
					}
				} catch {
					//execute the command disregarding permissions
					player.SendErrorMessage(alias.UsageHelpText);
				}
			}
		}

		/// <summary>
		/// Occurs when someone executes an alias command
		/// </summary>
		public static void ChatCommand_AliasExecuted(TShockAPI.CommandArgs e)
		{
			string commandIdentifier = e.Message;

			if (!string.IsNullOrEmpty(e.Message)) {
				commandIdentifier = e.Message.Split(' ').FirstOrDefault();
			}

			if (AliasExecuted != null) {
				AliasExecutedEventArgs args = new AliasExecutedEventArgs() {
					CommandIdentifier = commandIdentifier,
					CommandArgs = e
				};

				AliasExecuted(null, args);
			}
		}

		/// <summary>
		/// This is a copy of TShocks handlecommand method, sans the permission checks
		/// </summary>
		public static bool HandleCommandWithoutPermissions(TShockAPI.TSPlayer player, string text)
		{
			if (string.IsNullOrEmpty(text)) {
				return false;
			}
			string cmdText = text.Remove(0, 1);
			var args = SEconomyPlugin.CallPrivateMethod<List<string>>(typeof(TShockAPI.Commands), true, "ParseParameters", cmdText);

			if (args.Count < 1)
				return false;

			string cmdName = args[0].ToLower();
			args.RemoveAt(0);

			IEnumerable<TShockAPI.Command> cmds = TShockAPI.Commands.ChatCommands.Where(c => c.HasAlias(cmdName));

			if (Enumerable.Count(cmds) == 0) {
				if (player.AwaitingResponse.ContainsKey(cmdName)) {
					Action<TShockAPI.CommandArgs> call = player.AwaitingResponse[cmdName];
					player.AwaitingResponse.Remove(cmdName);
					call(new TShockAPI.CommandArgs(cmdText, player, args));
					return true;
				}
				player.SendErrorMessage("Invalid command entered. Type /help for a list of valid commands.");
				return true;
			}
			foreach (TShockAPI.Command cmd in cmds) {
				if (!cmd.AllowServer && !player.RealPlayer) {
					player.SendErrorMessage("You must use this command in-game.");
				} else {
					if (cmd.DoLog)
						TShockAPI.TShock.Utils.SendLogs(string.Format("{0} executed: /{1}.", player.Name, cmdText), Color.Red);
					cmd.RunWithoutPermissions(cmdText, player, args);
				}
			}
			return true;
		}


	}
}
