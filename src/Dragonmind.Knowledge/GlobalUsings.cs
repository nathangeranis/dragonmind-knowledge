// Dragonmind.Knowledge - Consolidated Knowledge Context (RAG + Knowledge Graph)
// Contains Domain, Application, and Infrastructure layers.
//
// One file per project, as CLAUDE.md says. System namespaces are absent on purpose: this project
// sets <ImplicitUsings>enable</ImplicitUsings>, which already provides System, System.Collections
// .Generic, System.Linq, System.Threading and System.Threading.Tasks. Only namespaces the SDK does
// not imply are listed here.

global using System.Globalization;

// Core dependencies
global using Dragonmind.Core.AI;
global using Dragonmind.Core.Application;
global using Dragonmind.Core.Application.AntiCorruptionLayer;
global using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;
global using Dragonmind.Core.Domain;
global using Dragonmind.Core.Domain.SharedIdentities;

// Knowledge domain
global using Dragonmind.Knowledge.Domain;
global using Dragonmind.Knowledge.Domain.Aggregates;
global using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;
global using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;
global using Dragonmind.Knowledge.Domain.Repositories;
global using Dragonmind.Knowledge.Domain.ValueObjects;

// Knowledge application
global using Dragonmind.Knowledge.Application.Commands;
global using Dragonmind.Knowledge.Application.Queries;
