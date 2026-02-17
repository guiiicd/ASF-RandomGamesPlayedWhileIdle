using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using SteamKit2;
using System.Text.Json.Nodes;
using System.IO;

namespace RandomGamesPlayedWhileIdle {
	[Export(typeof(IPlugin))]
	public sealed class RandomGamesPlayedWhileIdlePlugin : IBotConnection, IDisposable {
		private const int MaxGamesPlayedConcurrently = 32;
		private const int RotationIntervalMinutes = 1440;

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
					// Tenta ler do arquivo de configuração para garantir que pegamos apenas os jogos realmente fixos,
					// ignorando quaisquer modificações em memória feitas anteriormente pelo plugin.
					ImmutableList<uint> fixedGames = ImmutableList<uint>.Empty;
					bool loadedFromDisk = false;
					
					try {
						string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", $"{bot.BotName}.json");
						if (File.Exists(configPath)) {
							string json = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
							JsonNode? jsonNode = JsonNode.Parse(json);
							
							if (jsonNode != null) {
								loadedFromDisk = true; // Arquivo lido com sucesso, assumimos que o conteúdo (ou falta dele) é a verdade
								JsonNode? gamesNode = jsonNode["GamesPlayedWhileIdle"];
								
								if (gamesNode is JsonArray arr) {
									try {
										fixedGames = arr.Select(x => x?.GetValue<uint>() ?? 0).Where(x => x > 0).ToImmutableList();
										ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Lendo configuração do disco: {fixedGames.Count} jogos encontrados.");
									} catch {
										// Fallback for older .NET or mixed types
										fixedGames = arr.Select(x => uint.TryParse(x?.ToString(), out uint v) ? v : 0).Where(x => x > 0).ToImmutableList();
										ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] Aviso: método alternativo de parsing usado.");
									}
								} else {
									// Se a chave não existe ou não é array, fixedGames continua vazio (correto para quem não configurou nada)
									ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Nenhuma configuração de jogos encontrada no disco. Assumindo 0 jogos fixos.");
								}
							}
						}
					} catch (Exception ex) {
						ASF.ArchiLogger.LogGenericError($"[{bot.BotName}] Erro ao ler configuração do disco: {ex.Message}. Usando configuração em memória.");
					}

					// Se falhou ao ler do disco (arquivo não existe ou erro), fallback para memória.
					// Se leu do disco com sucesso e veio vazio, NÃO entramos aqui (evita pegar o lixo da memória).
					if (!loadedFromDisk) {
						fixedGames = bot.BotConfig.GamesPlayedWhileIdle;
						ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Usando configuração em memória (potencialmente suja): {fixedGames.Count} jogos.");
					}

					FixedGamesPerBot[bot.BotName] = fixedGames;
					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Jogos fixos configurados: {string.Join(", ", fixedGames)}");
				}

				// Busca a lista de jogos da biblioteca do bot via API
				Dictionary<uint, string>? ownedGames = await bot.ArchiHandler.GetOwnedGames(bot.SteamID).ConfigureAwait(false);

				if (ownedGames == null || ownedGames.Count == 0) {
					ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] Não foi possível obter a lista de jogos. Perfil pode estar privado ou sem jogos.");
					return;
				}

				OwnedGamesPerBot[bot.BotName] = ownedGames.Keys.ToList();
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Total de jogos na biblioteca: {ownedGames.Count}");

				// Define os jogos iniciais
				UpdateGamesPlayed(bot);

				// Inicia o timer de rotação
				StartRotationTimer(bot);
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
	}
}
