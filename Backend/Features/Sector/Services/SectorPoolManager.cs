﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mod.DynamicEncounters.Common;
using Mod.DynamicEncounters.Features.Common.Interfaces;
using Mod.DynamicEncounters.Features.Events.Data;
using Mod.DynamicEncounters.Features.Events.Interfaces;
using Mod.DynamicEncounters.Features.Scripts.Actions.Data;
using Mod.DynamicEncounters.Features.Scripts.Actions.Interfaces;
using Mod.DynamicEncounters.Features.Sector.Data;
using Mod.DynamicEncounters.Features.Sector.Interfaces;
using Mod.DynamicEncounters.Helpers;
using NQ;
using NQ.Interfaces;

namespace Mod.DynamicEncounters.Features.Sector.Services;

public class SectorPoolManager(IServiceProvider serviceProvider) : ISectorPoolManager
{
    public const double SectorGridSnap = DistanceHelpers.OneSuInMeters * 20;

    private readonly IRandomProvider _randomProvider = serviceProvider.GetRequiredService<IRandomProvider>();

    private readonly ISectorInstanceRepository _sectorInstanceRepository =
        serviceProvider.GetRequiredService<ISectorInstanceRepository>();

    private readonly IConstructHandleManager _constructHandleManager =
        serviceProvider.GetRequiredService<IConstructHandleManager>();

    private readonly IConstructSpatialHashRepository _constructSpatial =
        serviceProvider.GetRequiredService<IConstructSpatialHashRepository>();

    private readonly ILogger<SectorPoolManager> _logger = serviceProvider.CreateLogger<SectorPoolManager>();

    public async Task GenerateSectors(SectorGenerationArgs args)
    {
        var count = await _sectorInstanceRepository.GetCountWithTagAsync(args.Tag);
        var missingQuantity = args.Quantity - count;

        if (missingQuantity <= 0)
        {
            return;
        }

        var allSectorInstances = await _sectorInstanceRepository.GetAllAsync();
        var sectorInstanceMap = allSectorInstances
            .Select(k => k.Sector.GridSnap(SectorGridSnap * args.SectorMinimumGap))
            .ToHashSet();

        var random = _randomProvider.GetRandom();

        var randomMinutes = random.Next(0, 60);

        for (var i = 0; i < missingQuantity; i++)
        {
            if (!args.Encounters.Any())
            {
                continue;
            }
            
            var encounter = random.PickOneAtRandom(args.Encounters);

            // TODO
            var radius = MathFunctions.Lerp(
                encounter.Properties.MinRadius,
                encounter.Properties.MaxRadius,
                random.NextDouble()
            );

            Vec3 position;
            var interactions = 0;
            const int maxInteractions = 100;

            do
            {
                position = random.RandomDirectionVec3() * radius;
                position += encounter.Properties.CenterPosition;
                position = position.GridSnap(SectorGridSnap);

                interactions++;
            } while (interactions < maxInteractions ||
                     sectorInstanceMap.Contains(position.GridSnap(SectorGridSnap * args.SectorMinimumGap)));

            var instance = new SectorInstance
            {
                Id = Guid.NewGuid(),
                Sector = position,
                FactionId = args.FactionId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow + encounter.Properties.ExpirationTimeSpan +
                            TimeSpan.FromMinutes(randomMinutes * i),
                OnLoadScript = encounter.OnLoadScript,
                OnSectorEnterScript = encounter.OnSectorEnterScript,
            };

            await _sectorInstanceRepository.AddAsync(instance);
        }
    }

    public async Task LoadUnloadedSectors()
    {
        var scriptService = serviceProvider.GetRequiredService<IScriptService>();
        var unloadedSectors = (await _sectorInstanceRepository.FindUnloadedAsync()).ToList();

        if (unloadedSectors.Count == 0)
        {
            _logger.LogDebug("No Sectors {Count} Need Loading", unloadedSectors.Count);
            return;
        }

        foreach (var sector in unloadedSectors)
        {
            try
            {
                await scriptService.ExecuteScriptAsync(
                    sector.OnLoadScript,
                    new ScriptContext(
                        serviceProvider,
                        sector.FactionId,
                        [],
                        sector.Sector
                    )
                );

                await _sectorInstanceRepository.SetLoadedAsync(sector.Id, true);

                _logger.LogInformation("Loaded Sector {Id}({Sector})", sector.Id, sector.Sector);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to Load Sector {Id}({Sector})", sector.Id, sector.Sector);

                await _sectorInstanceRepository.SetLoadedAsync(sector.Id, false);
                throw;
            }
        }
    }

    public async Task ExecuteSectorCleanup()
    {
        var expiredSectors = await _sectorInstanceRepository.FindExpiredAsync();

        foreach (var sector in expiredSectors)
        {
            var players = await _constructSpatial.FindPlayerLiveConstructsOnSector(sector.Sector);
            if (!sector.IsForceExpired(DateTime.UtcNow) && players.Any())
            {
                _logger.LogInformation("Players Nearby - Extended Expiration of {Sector} {SectorGuid}", sector.Sector,
                    sector.Id);
                await _sectorInstanceRepository.SetExpirationFromNowAsync(sector.Id, TimeSpan.FromMinutes(60));
                continue;
            }

            await _constructHandleManager.CleanupConstructHandlesInSectorAsync(ModBase.Bot, sector.Sector);
        }

        await _sectorInstanceRepository.DeleteExpiredAsync();

        _logger.LogDebug("Executed Sector Cleanup");
    }

    public async Task SetExpirationFromNow(Vec3 sector, TimeSpan span)
    {
        var instance = await _sectorInstanceRepository.FindBySector(sector);

        if (instance == null)
        {
            return;
        }

        await _sectorInstanceRepository.SetExpirationFromNowAsync(instance.Id, span);

        _logger.LogInformation(
            "Set Sector expiration for {Sector}({Id}) to {Minutes} from now",
            instance.Sector,
            instance.Id,
            span
        );
    }

    public async Task ActivateEnteredSectors()
    {
        var sectorsToActivate = (await _sectorInstanceRepository.FindSectorsRequiringStartupAsync()).ToList();

        if (!sectorsToActivate.Any())
        {
            _logger.LogDebug("No sectors need startup");
            return;
        }

        var spatialHashRepository = serviceProvider.GetRequiredService<IConstructSpatialHashRepository>();

        var scriptService = serviceProvider.GetRequiredService<IScriptService>();
        var eventService = serviceProvider.GetRequiredService<IEventService>();
        var random = serviceProvider.GetRandomProvider().GetRandom();
        var orleans = serviceProvider.GetOrleans();

        foreach (var sectorInstance in sectorsToActivate)
        {
            var constructs = (await spatialHashRepository
                .FindPlayerLiveConstructsOnSector(sectorInstance.Sector))
                .ToList();

            if (constructs.Count == 0)
            {
                continue;
            }

            HashSet<ulong> playerIds = [];

            try
            {
                var queryPilotsTasks = constructs
                    .Select(x => orleans.GetConstructInfoGrain(x)).Select(x => x.Get());
                
                playerIds = (await Task.WhenAll(queryPilotsTasks))
                    .Select(x => x.mutableData.pilot)
                    .Where(x => x.HasValue)
                    .Select(x => x.Value.id)
                    .ToHashSet();
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Failed to query player IDS for Sector Startup. Sector will startup without that information");
            }

            _logger.LogInformation(
                "Starting up sector({Sector}) encounter: '{Encounter}'",
                sectorInstance.Sector,
                sectorInstance.OnSectorEnterScript
            );

            try
            {
                await scriptService.ExecuteScriptAsync(
                    sectorInstance.OnSectorEnterScript,
                    new ScriptContext(
                        serviceProvider,
                        sectorInstance.FactionId,
                        [],
                        sectorInstance.Sector
                    )
                    {
                        PlayerIds = playerIds
                    }
                );

                await _sectorInstanceRepository.TagAsStartedAsync(sectorInstance.Id);
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Failed to start encounter({Encounter}) at sector({Sector})",
                    sectorInstance.OnSectorEnterScript,
                    sectorInstance.Sector
                );
                throw;
            }

            try
            {
                ulong? playerId = null;
                if (playerIds.Count > 0)
                {
                    playerId = random.PickOneAtRandom(playerIds);
                }

                await eventService.PublishAsync(
                    new SectorActivatedEvent(
                        playerIds,
                        playerId,
                        sectorInstance.Sector,
                        random.PickOneAtRandom(constructs),
                        playerIds.Count
                    )
                );
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to publish {Event}", nameof(SectorActivatedEvent));
            }
        }
    }

    private Task ExpireSector(SectorInstance instance)
    {
        return _sectorInstanceRepository.DeleteAsync(instance.Id);
    }

    private struct ConstructSectorRow
    {
        public ulong id { get; set; }
        public double sector_x { get; set; }
        public double sector_y { get; set; }
        public double sector_z { get; set; }

        public Vec3 SectorToVec3() => new() { x = sector_x, y = sector_y, z = sector_z };
    }
}