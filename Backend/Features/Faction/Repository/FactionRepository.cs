﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Mod.DynamicEncounters.Database.Interfaces;
using Mod.DynamicEncounters.Features.Faction.Data;
using Mod.DynamicEncounters.Features.Faction.Interfaces;
using Newtonsoft.Json;

namespace Mod.DynamicEncounters.Features.Faction.Repository;

public class FactionRepository(IServiceProvider provider) : IFactionRepository
{
    private readonly IPostgresConnectionFactory _factory = provider.GetRequiredService<IPostgresConnectionFactory>();
    
    public async Task<IEnumerable<FactionItem>> GetAllAsync()
    {
        using var db = _factory.Create();
        db.Open();

        var result = (await db.QueryAsync<DbRow>(
            """
            SELECT * FROM public.mod_faction
            """
        )).ToList();

        return result.Select(MapToModel);
    }

    private FactionItem MapToModel(DbRow row)
    {
        return new FactionItem
        {
            Id = row.id,
            Tag = row.tag,
            Name = row.name,
            OrganizationId = (ulong?)row.organization_id,
            PlayerId = (ulong)row.player_id,
            Properties = JsonConvert.DeserializeObject<FactionItem.FactionProperties>(row.json_properties)
        };
    }

    public struct DbRow
    {
        public long id;
        public string name;
        public string tag;
        public long player_id;
        public long? organization_id;
        public string json_properties;
    }
}