using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Integration;
using ArchiSteamFarm.Web.Responses;
using SteamKit2;

namespace RandomGamesPlayedWhileIdle {
	[Export(typeof(IPlugin))]
	public sealed partial class RandomGamesPlayedWhileIdlePlugin : IBotConnection, IDisposable {
		private const int MaxGamesPlayedConcurrently = 32;
		private const int RotationIntervalMinutes = 30;

		private readonly ConcurrentDictionary<string, ImmutableList<uint>> FixedGamesPerBot = new();
		private readonly ConcurrentDictionary<string, List<uint>> OwnedGamesPerBot = new();
		private readonly ConcurrentDictionary<string, Timer> RotationTimers = new();

		public string Name => nameof(RandomGamesPlayedWhileIdle);
		public Version Version => typeof(RandomGamesPlayedWhileIdlePlugin).Assembly.GetName().Version!;

		public Task OnLoaded() => Task.CompletedTask;

		public async Task OnBotDisconnected(Bot bot, EResult reason) {
			ArgumentNullException.ThrowIfNull(bot);

			if (RotationTimers.TryRemove(bot.BotName, out Timer? timer)) {
				await timer.DisposeAsync().ConfigureAwait(false);
			}
		}

		public async Task OnBotLoggedOn(Bot bot) {
			ArgumentNullException.ThrowIfNull(bot);

			try {
				// Captura os jogos fixos configurados originalmente (apenas na primeira vez)
				if (!FixedGamesPerBot.ContainsKey(bot.BotName)) {
					ImmutableList<uint> fixedGames = bot.BotConfig.GamesPlayedWhileIdle;
					FixedGamesPerBot[bot.BotName] = fixedGames;
					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Jogos fixos configurados: {string.Join(", ", fixedGames)}");
				}

				// Busca a lista de jogos da biblioteca do bot
				using HtmlDocumentResponse? response = await bot.ArchiWebHandler
					.UrlGetToHtmlDocumentWithSession(new Uri(ArchiWebHandler.SteamCommunityURL,
						$"profiles/{bot.SteamID}/games")).ConfigureAwait(false);

				if (response?.Content?.QuerySelector("#gameslist_config") is IElement element) {
					List<uint> ownedGames = GamesListRegex()
						.Matches(element.OuterHtml)
						.Select(static x => uint.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture))
						.ToList();

					if (ownedGames.Count > 0) {
						OwnedGamesPerBot[bot.BotName] = ownedGames;
						ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Total de jogos na biblioteca: {ownedGames.Count}");

						// Define os jogos iniciais
						UpdateGamesPlayed(bot);

						// Inicia o timer de rotação
						StartRotationTimer(bot);
					}
				}
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}

		private void UpdateGamesPlayed(Bot bot) {
			if (!FixedGamesPerBot.TryGetValue(bot.BotName, out ImmutableList<uint>? fixedGames)) {
				fixedGames = ImmutableList<uint>.Empty;
			}

			if (!OwnedGamesPerBot.TryGetValue(bot.BotName, out List<uint>? ownedGames) || ownedGames.Count == 0) {
				return;
			}

			// Calcula quantos slots sobraram para jogos aleatórios
			int fixedCount = fixedGames.Count;
			int randomSlotsAvailable = MaxGamesPlayedConcurrently - fixedCount;

			// Filtra jogos que não estão na lista fixa para serem candidatos aleatórios
			List<uint> randomCandidates = ownedGames
				.Where(g => !fixedGames.Contains(g))
				.OrderBy(_ => Guid.NewGuid())
				.Take(randomSlotsAvailable)
				.ToList();

			// Combina jogos fixos + jogos aleatórios
			List<uint> finalGamesList = fixedGames.Concat(randomCandidates).ToList();

			bot.BotConfig.GetType().GetProperty("GamesPlayedWhileIdle")?.SetValue(
				bot.BotConfig,
				finalGamesList.ToImmutableList()
			);

			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Jogos atualizados: {fixedCount} fixos + {randomCandidates.Count} aleatórios = {finalGamesList.Count} total");
		}

		private void StartRotationTimer(Bot bot) {
			// Remove timer anterior se existir
			if (RotationTimers.TryRemove(bot.BotName, out Timer? existingTimer)) {
				existingTimer.Dispose();
			}

			Timer timer = new(
				_ => {
					try {
						ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Rotacionando jogos aleatórios...");
						UpdateGamesPlayed(bot);
					} catch (Exception e) {
						ASF.ArchiLogger.LogGenericException(e);
					}
				},
				null,
				TimeSpan.FromMinutes(RotationIntervalMinutes),
				TimeSpan.FromMinutes(RotationIntervalMinutes)
			);

			RotationTimers[bot.BotName] = timer;
		}

		public void Dispose() {
			foreach (Timer timer in RotationTimers.Values) {
				timer.Dispose();
			}
			RotationTimers.Clear();
		}

		[GeneratedRegex(@"{&quot;appid&quot;:(\d+),&quot;name&quot;:&quot;")]
		private static partial Regex GamesListRegex();
	}
}
