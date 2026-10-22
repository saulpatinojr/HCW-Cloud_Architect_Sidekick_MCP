# F. MCP tool contracts

_Generated from `src/AdrMcp.Application/Tools/ToolCatalog.cs` by `tools/AdrMcp.DocGen`. Do not edit by hand._

Every tool returns a `ToolEnvelope`: `{ ok, data?, error?: { code, message, retryable, correlationId, details? }, meta: { correlationId, tool, durationMs, warnings?, idempotentReplay? } }` as the text content of the MCP `CallToolResult`. Outputs are intentionally compact (summaries, truncated excerpts, capped lists) to protect the client context window. `workspaceId` is optional everywhere and must be one the caller is authorized for; tenant identity is **never** taken from arguments. Enum values are PascalCase (e.g. `Accepted`, `Drifted`, `Inferred`).

## Overview

| Tool | Phase | Implemented | Mutating | Idempotency | Permission |
|---|---|---|---|---|---|
| `adr_create` | Mvp | yes | mutating | Key | AdrCreate |
| `adr_get` | Mvp | yes | read-only | ReadOnly | AdrRead |
| `adr_list` | Mvp | yes | read-only | ReadOnly | AdrRead |
| `adr_search` | Mvp | yes | read-only | ReadOnly | AdrRead |
| `adr_update` | Extended | yes | mutating | Key | AdrEdit |
| `adr_propose_change` | Mvp | yes | mutating | Key | AdrPropose |
| `adr_approve_change` | Mvp | yes | mutating | Key | AdrApproveChange |
| `adr_reject_change` | Extended | yes | mutating | Key | AdrApproveChange |
| `adr_supersede` | Extended | yes | mutating | Key | AdrSupersede |
| `adr_archive` | Extended | yes | mutating | Key | AdrArchive |
| `adr_history` | Extended | yes | read-only | ReadOnly | AdrRead |
| `adr_import` | Extended | yes | mutating | Key | AdrImport |
| `adr_export` | Extended | yes | read-only | ReadOnly | AdrRead |
| `file_upload_request` | Mvp | yes | mutating | Key | EvidenceUpload |
| `file_register_upload` | Mvp | yes | mutating | Natural | EvidenceUpload |
| `file_import_from_repository` | Later | specified only | mutating | Key | EvidenceUpload |
| `evidence_attach` | Mvp | yes | mutating | Key | EvidenceUpload |
| `evidence_list` | Extended | yes | read-only | ReadOnly | AdrRead |
| `evidence_remove` | Extended | yes | mutating | Key | EvidenceRemove |
| `technology_detect` | Mvp | yes | read-only | ReadOnly | AnalysisRun |
| `technology_refresh` | Mvp | yes | mutating | Key | AnalysisRun |
| `adr_find_related_code` | Later | specified only | read-only | ReadOnly | AdrRead |
| `adr_detect_drift` | Mvp | yes | mutating | Natural | AnalysisRun |
| `adr_check_contradictions` | Mvp | yes | mutating | Natural | AnalysisRun |
| `adr_safety_review` | Mvp | yes | mutating | Natural | AnalysisRun |
| `finding_review` | Mvp | yes | mutating | Key | FindingReview |
| `adr_impact_analysis` | Later | specified only | read-only | ReadOnly | AdrRead |
| `architecture_fitness_check` | Later | specified only | read-only | ReadOnly | AnalysisRun |
| `dependency_map_generate` | Later | specified only | mutating | Key | AnalysisRun |
| `c4_model_generate` | Later | specified only | mutating | Key | AnalysisRun |
| `decision_memory_search` | Extended | yes | read-only | ReadOnly | MemoryRead |
| `decision_lineage_get` | Later | specified only | read-only | ReadOnly | AdrRead |
| `architecture_rule_create` | Later | specified only | mutating | Key | RuleManage |
| `architecture_rule_validate` | Later | specified only | read-only | ReadOnly | AnalysisRun |
| `governance_check` | Later | specified only | read-only | ReadOnly | AdrRead |
| `deployment_readiness_check` | Later | specified only | read-only | ReadOnly | AnalysisRun |
| `analysis_job_get` | Mvp | yes | read-only | ReadOnly | JobRead |
| `analysis_job_cancel` | Extended | yes | mutating | Natural | JobCancel |
| `analysis_job_results` | Extended | yes | read-only | ReadOnly | JobRead |

Idempotency modes: **Key** = client-supplied `idempotencyKey`; same key + same payload replays the stored result (24 h), same key + different payload → `IDEMPOTENCY_CONFLICT`. **Natural** = the operation is idempotent by its own identifiers (e.g. registering the same upload twice returns the recorded outcome; a drift scan is a merge). **ReadOnly** = no side effects beyond audit/memory records.

Authorization roles: Reader ⊂ Author ⊂ Approver ⊂ Admin (see `RolePermissions`). In `EntraBuiltIn` mode roles come from Entra app roles `Adr.Reader`, `Adr.Author`, `Adr.Approver`, `Adr.Admin`; in `FunctionKey` mode from `Auth:StaticRoles`.

## `adr_create`

**Purpose.** Create a new Architectural Decision Record in Draft or Proposed status from Markdown (MADR-style) and/or structured fields. Human-owned content is stored verbatim; the original Markdown is kept as the source of record.

- Classification: **mutating**, phase **Mvp**, implemented
- Required permission: `AdrCreate`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `CONFLICT`, `PAYLOAD_TOO_LARGE`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "markdown": {
      "type": "string",
      "description": "Full ADR Markdown (\u003C= 200,000 chars). Headings recognised: Context, Decision, Rationale/Decision Drivers, Considered Options/Alternatives, Consequences, Status.",
      "maxLength": 200000
    },
    "fields": {
      "type": "object",
      "description": "Structured fields; override parsed Markdown when both are given. title is required when markdown is omitted.",
      "additionalProperties": false,
      "properties": {
        "title": {
          "type": "string",
          "description": "Title.",
          "maxLength": 300
        },
        "context": {
          "type": "string",
          "description": "Context / problem statement.",
          "maxLength": 20000
        },
        "decision": {
          "type": "string",
          "description": "The decision.",
          "maxLength": 20000
        },
        "rationale": {
          "type": "string",
          "description": "Why.",
          "maxLength": 20000
        },
        "alternatives": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Considered alternatives.",
          "maxItems": 50
        },
        "consequences": {
          "type": "string",
          "description": "Consequences.",
          "maxLength": 20000
        },
        "status": {
          "type": "string",
          "description": "Lifecycle status.",
          "enum": [
            "Draft",
            "Proposed",
            "UnderReview",
            "Accepted",
            "Rejected",
            "Deprecated",
            "Superseded",
            "Archived"
          ]
        },
        "owners": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Owners.",
          "maxItems": 50
        },
        "approvers": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Approvers.",
          "maxItems": 50
        },
        "approvalResult": {
          "type": "string",
          "description": "Approval outcome text.",
          "maxLength": 2000
        },
        "humanNotes": {
          "type": "string",
          "description": "Free notes.",
          "maxLength": 20000
        },
        "tags": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Tags.",
          "maxItems": 50
        },
        "claims": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "claimId": {
                "type": "string",
                "description": "Stable id for the claim within the ADR.",
                "maxLength": 64
              },
              "kind": {
                "type": "string",
                "description": "Claim kind.",
                "enum": [
                  "ProhibitedTechnology",
                  "RequiredTechnology",
                  "InfrastructureRequirement",
                  "DatabaseOwnership",
                  "IntegrationStyle",
                  "CloudProvider",
                  "AuthStrategy",
                  "DataResidency",
                  "DataRetention",
                  "Other"
                ]
              },
              "subject": {
                "type": "string",
                "description": "What the claim is about, e.g. \u0027Newtonsoft.Json\u0027, \u0027storage accounts\u0027, \u0027OrderService\u0027.",
                "maxLength": 200
              },
              "predicate": {
                "type": "string",
                "description": "Relation, e.g. \u0027must have\u0027, \u0027owns\u0027.",
                "maxLength": 100
              },
              "object": {
                "type": "string",
                "description": "Target value, e.g. \u0027private endpoint\u0027, \u0027OrdersDb\u0027, \u0027async\u0027.",
                "maxLength": 200
              },
              "scope": {
                "type": "object",
                "properties": {
                  "system": {
                    "type": "string",
                    "description": "System the claim applies to; omit for all.",
                    "maxLength": 100
                  },
                  "boundedContext": {
                    "type": "string",
                    "description": "Bounded context; omit for all.",
                    "maxLength": 100
                  },
                  "environment": {
                    "type": "string",
                    "description": "Environment such as production; omit for all.",
                    "maxLength": 50
                  },
                  "effectiveFrom": {
                    "type": "string",
                    "description": "ISO-8601 date-time."
                  },
                  "effectiveTo": {
                    "type": "string",
                    "description": "ISO-8601 date-time."
                  }
                },
                "additionalProperties": false
              },
              "sourceText": {
                "type": "string",
                "description": "Sentence in the decision this claim comes from.",
                "maxLength": 1000
              }
            },
            "additionalProperties": false,
            "required": [
              "claimId",
              "kind",
              "subject"
            ]
          },
          "description": "Structured, testable claims that drive drift and contradiction detection.",
          "maxItems": 200
        },
        "supersedes": {
          "type": "string",
          "description": "ADR id this one supersedes.",
          "maxLength": 128
        },
        "supersededBy": {
          "type": "string",
          "description": "ADR id that supersedes this one.",
          "maxLength": 128
        }
      }
    },
    "repositoryId": {
      "type": "string",
      "description": "Optional repository identifier this ADR belongs to.",
      "maxLength": 128
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "adr": {
          "type": "object",
          "properties": {
            "summary": {
              "type": "object",
              "properties": {
                "id": {
                  "type": "string",
                  "description": "ADR id."
                },
                "adrNumber": {
                  "type": "integer",
                  "description": "Sequence number within workspace."
                },
                "title": {
                  "type": "string",
                  "description": "Title."
                },
                "status": {
                  "type": "string",
                  "description": "Lifecycle status.",
                  "enum": [
                    "Draft",
                    "Proposed",
                    "UnderReview",
                    "Accepted",
                    "Rejected",
                    "Deprecated",
                    "Superseded",
                    "Archived"
                  ]
                },
                "tags": {
                  "type": "array",
                  "items": {
                    "type": "string",
                    "maxLength": 500
                  },
                  "description": "Tags.",
                  "maxItems": 50
                },
                "updatedAt": {
                  "type": "string",
                  "description": "ISO-8601."
                },
                "humanRevision": {
                  "type": "integer",
                  "description": "Human-owned revision."
                },
                "observedRevision": {
                  "type": "integer",
                  "description": "System-managed revision."
                },
                "driftStatus": {
                  "type": "string",
                  "description": "NotScanned, Compliant, Drifted, Uncertain, Unsupported or NoClaims."
                },
                "openContradictions": {
                  "type": "integer",
                  "description": "Open contradiction findings."
                },
                "openDriftFindings": {
                  "type": "integer",
                  "description": "Open drift findings."
                },
                "etag": {
                  "type": "string",
                  "description": "Concurrency token; pass back as expectedEtag on updates."
                }
              },
              "additionalProperties": false
            },
            "context": {
              "type": "string",
              "description": ""
            },
            "decision": {
              "type": "string",
              "description": ""
            },
            "rationale": {
              "type": "string",
              "description": ""
            },
            "alternatives": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "",
              "maxItems": 50
            },
            "consequences": {
              "type": "string",
              "description": ""
            },
            "owners": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "",
              "maxItems": 50
            },
            "approvers": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "",
              "maxItems": 50
            },
            "approvalResult": {
              "type": "string",
              "description": ""
            },
            "humanNotes": {
              "type": "string",
              "description": ""
            },
            "claims": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "claimId": {
                    "type": "string",
                    "description": "Stable id for the claim within the ADR.",
                    "maxLength": 64
                  },
                  "kind": {
                    "type": "string",
                    "description": "Claim kind.",
                    "enum": [
                      "ProhibitedTechnology",
                      "RequiredTechnology",
                      "InfrastructureRequirement",
                      "DatabaseOwnership",
                      "IntegrationStyle",
                      "CloudProvider",
                      "AuthStrategy",
                      "DataResidency",
                      "DataRetention",
                      "Other"
                    ]
                  },
                  "subject": {
                    "type": "string",
                    "description": "What the claim is about, e.g. \u0027Newtonsoft.Json\u0027, \u0027storage accounts\u0027, \u0027OrderService\u0027.",
                    "maxLength": 200
                  },
                  "predicate": {
                    "type": "string",
                    "description": "Relation, e.g. \u0027must have\u0027, \u0027owns\u0027.",
                    "maxLength": 100
                  },
                  "object": {
                    "type": "string",
                    "description": "Target value, e.g. \u0027private endpoint\u0027, \u0027OrdersDb\u0027, \u0027async\u0027.",
                    "maxLength": 200
                  },
                  "scope": {
                    "type": "object",
                    "properties": {
                      "system": {
                        "type": "string",
                        "description": "System the claim applies to; omit for all.",
                        "maxLength": 100
                      },
                      "boundedContext": {
                        "type": "string",
                        "description": "Bounded context; omit for all.",
                        "maxLength": 100
                      },
                      "environment": {
                        "type": "string",
                        "description": "Environment such as production; omit for all.",
                        "maxLength": 50
                      },
                      "effectiveFrom": {
                        "type": "string",
                        "description": "ISO-8601 date-time."
                      },
                      "effectiveTo": {
                        "type": "string",
                        "description": "ISO-8601 date-time."
                      }
                    },
                    "additionalProperties": false
                  },
                  "sourceText": {
                    "type": "string",
                    "description": "Sentence in the decision this claim comes from.",
                    "maxLength": 1000
                  }
                },
                "additionalProperties": false,
                "required": [
                  "claimId",
                  "kind",
                  "subject"
                ]
              },
              "description": ""
            },
            "supersedes": {
              "type": "string",
              "description": ""
            },
            "supersededBy": {
              "type": "string",
              "description": ""
            },
            "createdAt": {
              "type": "string",
              "description": ""
            },
            "acceptedAt": {
              "type": "string",
              "description": ""
            },
            "contentHash": {
              "type": "string",
              "description": "SHA-256 of canonical human-owned content."
            },
            "technologies": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": {
                    "type": "string",
                    "description": "Normalized name."
                  },
                  "version": {
                    "type": "string",
                    "description": ""
                  },
                  "category": {
                    "type": "string",
                    "description": ""
                  },
                  "status": {
                    "type": "string",
                    "description": "Observed, Inferred, Declared, Removed or Uncertain"
                  },
                  "confidence": {
                    "type": "number",
                    "description": ""
                  },
                  "basis": {
                    "type": "string",
                    "description": ""
                  },
                  "sourceFile": {
                    "type": "string",
                    "description": ""
                  },
                  "detector": {
                    "type": "string",
                    "description": "name@version"
                  },
                  "firstObservedAt": {
                    "type": "string",
                    "description": ""
                  },
                  "lastObservedAt": {
                    "type": "string",
                    "description": ""
                  }
                },
                "additionalProperties": false
              },
              "description": "Top observed technologies (system-managed)"
            },
            "technologyCount": {
              "type": "integer",
              "description": ""
            },
            "evidenceCount": {
              "type": "integer",
              "description": ""
            },
            "evidenceIds": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "",
              "maxItems": 50
            },
            "safetyFindingCount": {
              "type": "integer",
              "description": ""
            },
            "lastAnalyzedAt": {
              "type": "string",
              "description": ""
            },
            "markdown": {
              "type": "string",
              "description": "Only when requested."
            },
            "provenance": {
              "description": "Last human change provenance."
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "fields": {
    "title": "Use Azure Service Bus for order events",
    "context": "Order and billing services need reliable async integration.",
    "decision": "We will use Azure Service Bus (Standard) topics for all order domain events.",
    "rationale": "Managed, supports sessions and dead-lettering; team has experience.",
    "alternatives": [
      "Event Hubs",
      "RabbitMQ on AKS"
    ],
    "consequences": "Vendor dependency on Azure. Rollback: switch publishers to the previous REST callbacks.",
    "status": "Proposed",
    "tags": [
      "messaging",
      "orders"
    ],
    "claims": [
      {
        "claimId": "c1",
        "kind": "RequiredTechnology",
        "subject": "Azure Service Bus",
        "scope": {
          "system": "Orders"
        }
      },
      {
        "claimId": "c2",
        "kind": "IntegrationStyle",
        "subject": "integration",
        "predicate": "is",
        "object": "async",
        "scope": {
          "system": "Orders"
        }
      }
    ]
  },
  "idempotencyKey": "create-asb-2026-10"
}
```

**Example response (`data`)**

```json
{
  "adr": {
    "summary": {
      "id": "adr-0007",
      "adrNumber": 7,
      "title": "Use Azure Service Bus for order events",
      "status": "Proposed",
      "tags": [
        "messaging",
        "orders"
      ],
      "updatedAt": "2026-10-07T04:10:03Z",
      "humanRevision": 1,
      "observedRevision": 0,
      "driftStatus": "NotScanned",
      "openContradictions": 0,
      "openDriftFindings": 0,
      "etag": "\u00220000a1b2-0000-0000-0000-000000000000\u0022"
    },
    "context": "Order and billing services need reliable async integration.",
    "decision": "We will use Azure Service Bus (Standard) topics for all order domain events.",
    "rationale": "Managed, supports sessions and dead-lettering; team has experience.",
    "alternatives": [
      "Event Hubs",
      "RabbitMQ on AKS"
    ],
    "consequences": "Vendor dependency on Azure. Rollback: switch publishers to the previous REST callbacks.",
    "owners": [],
    "approvers": [],
    "claims": [
      {
        "claimId": "c1",
        "kind": "RequiredTechnology",
        "subject": "Azure Service Bus",
        "scope": {
          "system": "Orders"
        },
        "declared": true
      }
    ],
    "createdAt": "2026-10-07T04:10:03Z",
    "contentHash": "6f1c\u2026",
    "technologies": [],
    "technologyCount": 0,
    "evidenceCount": 0,
    "evidenceIds": [],
    "safetyFindingCount": 0,
    "provenance": {
      "actor": "user:3f2a\u2026",
      "actorType": "human",
      "timestamp": "2026-10-07T04:10:03Z",
      "correlationId": "c7d1\u2026"
    }
  }
}
```

## `adr_get`

**Purpose.** Get one ADR with human-owned content, declared claims, and a compact view of system-managed observations. Optionally include the rendered Markdown.

- Classification: **read-only**, phase **Mvp**, implemented
- Required permission: `AdrRead`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `ADR_NOT_FOUND`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "includeMarkdown": {
      "type": "boolean",
      "description": "Include rendered Markdown (larger response).",
      "default": false
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "adr": {
          "type": "object",
          "properties": {
            "summary": {
              "type": "object",
              "properties": {
                "id": {
                  "type": "string",
                  "description": "ADR id."
                },
                "adrNumber": {
                  "type": "integer",
                  "description": "Sequence number within workspace."
                },
                "title": {
                  "type": "string",
                  "description": "Title."
                },
                "status": {
                  "type": "string",
                  "description": "Lifecycle status.",
                  "enum": [
                    "Draft",
                    "Proposed",
                    "UnderReview",
                    "Accepted",
                    "Rejected",
                    "Deprecated",
                    "Superseded",
                    "Archived"
                  ]
                },
                "tags": {
                  "type": "array",
                  "items": {
                    "type": "string",
                    "maxLength": 500
                  },
                  "description": "Tags.",
                  "maxItems": 50
                },
                "updatedAt": {
                  "type": "string",
                  "description": "ISO-8601."
                },
                "humanRevision": {
                  "type": "integer",
                  "description": "Human-owned revision."
                },
                "observedRevision": {
                  "type": "integer",
                  "description": "System-managed revision."
                },
                "driftStatus": {
                  "type": "string",
                  "description": "NotScanned, Compliant, Drifted, Uncertain, Unsupported or NoClaims."
                },
                "openContradictions": {
                  "type": "integer",
                  "description": "Open contradiction findings."
                },
                "openDriftFindings": {
                  "type": "integer",
                  "description": "Open drift findings."
                },
                "etag": {
                  "type": "string",
                  "description": "Concurrency token; pass back as expectedEtag on updates."
                }
              },
              "additionalProperties": false
            },
            "context": {
              "type": "string",
              "description": ""
            },
            "decision": {
              "type": "string",
              "description": ""
            },
            "rationale": {
              "type": "string",
              "description": ""
            },
            "alternatives": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "",
              "maxItems": 50
            },
            "consequences": {
              "type": "string",
              "description": ""
            },
            "owners": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "",
              "maxItems": 50
            },
            "approvers": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "",
              "maxItems": 50
            },
            "approvalResult": {
              "type": "string",
              "description": ""
            },
            "humanNotes": {
              "type": "string",
              "description": ""
            },
            "claims": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "claimId": {
                    "type": "string",
                    "description": "Stable id for the claim within the ADR.",
                    "maxLength": 64
                  },
                  "kind": {
                    "type": "string",
                    "description": "Claim kind.",
                    "enum": [
                      "ProhibitedTechnology",
                      "RequiredTechnology",
                      "InfrastructureRequirement",
                      "DatabaseOwnership",
                      "IntegrationStyle",
                      "CloudProvider",
                      "AuthStrategy",
                      "DataResidency",
                      "DataRetention",
                      "Other"
                    ]
                  },
                  "subject": {
                    "type": "string",
                    "description": "What the claim is about, e.g. \u0027Newtonsoft.Json\u0027, \u0027storage accounts\u0027, \u0027OrderService\u0027.",
                    "maxLength": 200
                  },
                  "predicate": {
                    "type": "string",
                    "description": "Relation, e.g. \u0027must have\u0027, \u0027owns\u0027.",
                    "maxLength": 100
                  },
                  "object": {
                    "type": "string",
                    "description": "Target value, e.g. \u0027private endpoint\u0027, \u0027OrdersDb\u0027, \u0027async\u0027.",
                    "maxLength": 200
                  },
                  "scope": {
                    "type": "object",
                    "properties": {
                      "system": {
                        "type": "string",
                        "description": "System the claim applies to; omit for all.",
                        "maxLength": 100
                      },
                      "boundedContext": {
                        "type": "string",
                        "description": "Bounded context; omit for all.",
                        "maxLength": 100
                      },
                      "environment": {
                        "type": "string",
                        "description": "Environment such as production; omit for all.",
                        "maxLength": 50
                      },
                      "effectiveFrom": {
                        "type": "string",
                        "description": "ISO-8601 date-time."
                      },
                      "effectiveTo": {
                        "type": "string",
                        "description": "ISO-8601 date-time."
                      }
                    },
                    "additionalProperties": false
                  },
                  "sourceText": {
                    "type": "string",
                    "description": "Sentence in the decision this claim comes from.",
                    "maxLength": 1000
                  }
                },
                "additionalProperties": false,
                "required": [
                  "claimId",
                  "kind",
                  "subject"
                ]
              },
              "description": ""
            },
            "supersedes": {
              "type": "string",
              "description": ""
            },
            "supersededBy": {
              "type": "string",
              "description": ""
            },
            "createdAt": {
              "type": "string",
              "description": ""
            },
            "acceptedAt": {
              "type": "string",
              "description": ""
            },
            "contentHash": {
              "type": "string",
              "description": "SHA-256 of canonical human-owned content."
            },
            "technologies": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": {
                    "type": "string",
                    "description": "Normalized name."
                  },
                  "version": {
                    "type": "string",
                    "description": ""
                  },
                  "category": {
                    "type": "string",
                    "description": ""
                  },
                  "status": {
                    "type": "string",
                    "description": "Observed, Inferred, Declared, Removed or Uncertain"
                  },
                  "confidence": {
                    "type": "number",
                    "description": ""
                  },
                  "basis": {
                    "type": "string",
                    "description": ""
                  },
                  "sourceFile": {
                    "type": "string",
                    "description": ""
                  },
                  "detector": {
                    "type": "string",
                    "description": "name@version"
                  },
                  "firstObservedAt": {
                    "type": "string",
                    "description": ""
                  },
                  "lastObservedAt": {
                    "type": "string",
                    "description": ""
                  }
                },
                "additionalProperties": false
              },
              "description": "Top observed technologies (system-managed)"
            },
            "technologyCount": {
              "type": "integer",
              "description": ""
            },
            "evidenceCount": {
              "type": "integer",
              "description": ""
            },
            "evidenceIds": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "",
              "maxItems": 50
            },
            "safetyFindingCount": {
              "type": "integer",
              "description": ""
            },
            "lastAnalyzedAt": {
              "type": "string",
              "description": ""
            },
            "markdown": {
              "type": "string",
              "description": "Only when requested."
            },
            "provenance": {
              "description": "Last human change provenance."
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "adrId": "adr-0007",
  "includeMarkdown": false
}
```

**Example response (`data`)**

```json
{
  "adr": {
    "summary": {
      "id": "adr-0007",
      "adrNumber": 7,
      "title": "Use Azure Service Bus for order events",
      "status": "Accepted",
      "tags": [
        "messaging",
        "orders"
      ],
      "updatedAt": "2026-10-07T05:00:00Z",
      "humanRevision": 3,
      "observedRevision": 4,
      "driftStatus": "Compliant",
      "openContradictions": 0,
      "openDriftFindings": 0,
      "etag": "\u0022\u2026\u0022"
    },
    "decision": "\u2026",
    "technologies": [
      {
        "name": "Azure Service Bus",
        "version": "7.18.2",
        "category": "Messaging",
        "status": "Observed",
        "confidence": 0.95,
        "basis": "Observed",
        "sourceFile": "src/Orders/Orders.csproj",
        "detector": "csproj@1.0.0",
        "firstObservedAt": "2026-10-07T04:30:00Z",
        "lastObservedAt": "2026-10-07T05:00:00Z"
      }
    ],
    "technologyCount": 12,
    "evidenceCount": 2,
    "evidenceIds": [
      "upl_\u2026"
    ],
    "safetyFindingCount": 1
  }
}
```

## `adr_list`

**Purpose.** List ADRs in a workspace with optional status/tag/owner filters. Returns compact summaries and a continuation token.

- Classification: **read-only**, phase **Mvp**, implemented
- Required permission: `AdrRead`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "status": {
      "type": "string",
      "description": "Filter by status.",
      "enum": [
        "Draft",
        "Proposed",
        "UnderReview",
        "Accepted",
        "Rejected",
        "Deprecated",
        "Superseded",
        "Archived"
      ]
    },
    "tag": {
      "type": "string",
      "description": "Filter by tag.",
      "maxLength": 100
    },
    "owner": {
      "type": "string",
      "description": "Filter by owner.",
      "maxLength": 200
    },
    "take": {
      "type": "integer",
      "description": "Page size.",
      "minimum": 1,
      "maximum": 50,
      "default": 25
    },
    "continuationToken": {
      "type": "string",
      "description": "Token from a previous page.",
      "maxLength": 4000
    }
  },
  "additionalProperties": false
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "items": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "id": {
                "type": "string",
                "description": "ADR id."
              },
              "adrNumber": {
                "type": "integer",
                "description": "Sequence number within workspace."
              },
              "title": {
                "type": "string",
                "description": "Title."
              },
              "status": {
                "type": "string",
                "description": "Lifecycle status.",
                "enum": [
                  "Draft",
                  "Proposed",
                  "UnderReview",
                  "Accepted",
                  "Rejected",
                  "Deprecated",
                  "Superseded",
                  "Archived"
                ]
              },
              "tags": {
                "type": "array",
                "items": {
                  "type": "string",
                  "maxLength": 500
                },
                "description": "Tags.",
                "maxItems": 50
              },
              "updatedAt": {
                "type": "string",
                "description": "ISO-8601."
              },
              "humanRevision": {
                "type": "integer",
                "description": "Human-owned revision."
              },
              "observedRevision": {
                "type": "integer",
                "description": "System-managed revision."
              },
              "driftStatus": {
                "type": "string",
                "description": "NotScanned, Compliant, Drifted, Uncertain, Unsupported or NoClaims."
              },
              "openContradictions": {
                "type": "integer",
                "description": "Open contradiction findings."
              },
              "openDriftFindings": {
                "type": "integer",
                "description": "Open drift findings."
              },
              "etag": {
                "type": "string",
                "description": "Concurrency token; pass back as expectedEtag on updates."
              }
            },
            "additionalProperties": false
          },
          "description": "ADR summaries"
        },
        "continuationToken": {
          "type": "string",
          "description": "Pass to fetch the next page; absent when exhausted."
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "status": "Accepted",
  "take": 2
}
```

**Example response (`data`)**

```json
{
  "items": [
    {
      "id": "adr-0007",
      "adrNumber": 7,
      "title": "Use Azure Service Bus for order events",
      "status": "Accepted",
      "tags": [
        "messaging"
      ],
      "updatedAt": "\u2026",
      "humanRevision": 3,
      "observedRevision": 4,
      "driftStatus": "Compliant",
      "openContradictions": 0,
      "openDriftFindings": 0,
      "etag": "\u0022\u2026\u0022"
    }
  ],
  "continuationToken": "2"
}
```

## `adr_search`

**Purpose.** Keyword search over ADR titles, tags, context, decision and consequences within a workspace. Returns ranked summaries with the matched terms. Deterministic scoring; no model involved.

- Classification: **read-only**, phase **Mvp**, implemented
- Required permission: `AdrRead`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "query": {
      "type": "string",
      "description": "Free-text query; up to 8 significant terms are used.",
      "maxLength": 500
    },
    "status": {
      "type": "string",
      "description": "Restrict to a status.",
      "enum": [
        "Draft",
        "Proposed",
        "UnderReview",
        "Accepted",
        "Rejected",
        "Deprecated",
        "Superseded",
        "Archived"
      ]
    },
    "take": {
      "type": "integer",
      "description": "Max results.",
      "minimum": 1,
      "maximum": 50,
      "default": 10
    }
  },
  "additionalProperties": false,
  "required": [
    "query"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "hits": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "adr": {
                "type": "object",
                "properties": {
                  "id": {
                    "type": "string",
                    "description": "ADR id."
                  },
                  "adrNumber": {
                    "type": "integer",
                    "description": "Sequence number within workspace."
                  },
                  "title": {
                    "type": "string",
                    "description": "Title."
                  },
                  "status": {
                    "type": "string",
                    "description": "Lifecycle status.",
                    "enum": [
                      "Draft",
                      "Proposed",
                      "UnderReview",
                      "Accepted",
                      "Rejected",
                      "Deprecated",
                      "Superseded",
                      "Archived"
                    ]
                  },
                  "tags": {
                    "type": "array",
                    "items": {
                      "type": "string",
                      "maxLength": 500
                    },
                    "description": "Tags.",
                    "maxItems": 50
                  },
                  "updatedAt": {
                    "type": "string",
                    "description": "ISO-8601."
                  },
                  "humanRevision": {
                    "type": "integer",
                    "description": "Human-owned revision."
                  },
                  "observedRevision": {
                    "type": "integer",
                    "description": "System-managed revision."
                  },
                  "driftStatus": {
                    "type": "string",
                    "description": "NotScanned, Compliant, Drifted, Uncertain, Unsupported or NoClaims."
                  },
                  "openContradictions": {
                    "type": "integer",
                    "description": "Open contradiction findings."
                  },
                  "openDriftFindings": {
                    "type": "integer",
                    "description": "Open drift findings."
                  },
                  "etag": {
                    "type": "string",
                    "description": "Concurrency token; pass back as expectedEtag on updates."
                  }
                },
                "additionalProperties": false
              },
              "score": {
                "type": "number",
                "description": "Relevance score."
              },
              "matchedTerms": {
                "type": "array",
                "items": {
                  "type": "string",
                  "maxLength": 500
                },
                "description": "Terms that matched.",
                "maxItems": 50
              }
            },
            "additionalProperties": false
          },
          "description": "Ranked hits"
        },
        "termsUsed": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Normalised search terms.",
          "maxItems": 50
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "query": "service bus async orders",
  "take": 5
}
```

**Example response (`data`)**

```json
{
  "hits": [
    {
      "adr": {
        "id": "adr-0007",
        "title": "Use Azure Service Bus for order events",
        "status": "Accepted"
      },
      "score": 8.4,
      "matchedTerms": [
        "service",
        "bus",
        "orders"
      ]
    }
  ],
  "termsUsed": [
    "service",
    "bus",
    "async",
    "orders"
  ]
}
```

## `adr_update`

**Purpose.** Directly edit human-owned fields as an authorized human author. Accepted ADRs' decision content is immutable (supersede instead). Status changes and supersession require Approver permission. Use expectedEtag for optimistic concurrency.

- Classification: **mutating**, phase **Extended**, implemented
- Required permission: `AdrEdit`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `ADR_NOT_FOUND`, `ETAG_MISMATCH`, `INVALID_STATUS_TRANSITION`, `CONFLICT`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "fields": {
      "type": "object",
      "description": "Fields to change; omitted fields stay unchanged.",
      "additionalProperties": false,
      "properties": {
        "title": {
          "type": "string",
          "description": "Title.",
          "maxLength": 300
        },
        "context": {
          "type": "string",
          "description": "Context / problem statement.",
          "maxLength": 20000
        },
        "decision": {
          "type": "string",
          "description": "The decision.",
          "maxLength": 20000
        },
        "rationale": {
          "type": "string",
          "description": "Why.",
          "maxLength": 20000
        },
        "alternatives": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Considered alternatives.",
          "maxItems": 50
        },
        "consequences": {
          "type": "string",
          "description": "Consequences.",
          "maxLength": 20000
        },
        "status": {
          "type": "string",
          "description": "Lifecycle status.",
          "enum": [
            "Draft",
            "Proposed",
            "UnderReview",
            "Accepted",
            "Rejected",
            "Deprecated",
            "Superseded",
            "Archived"
          ]
        },
        "owners": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Owners.",
          "maxItems": 50
        },
        "approvers": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Approvers.",
          "maxItems": 50
        },
        "approvalResult": {
          "type": "string",
          "description": "Approval outcome text.",
          "maxLength": 2000
        },
        "humanNotes": {
          "type": "string",
          "description": "Free notes.",
          "maxLength": 20000
        },
        "tags": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Tags.",
          "maxItems": 50
        },
        "claims": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "claimId": {
                "type": "string",
                "description": "Stable id for the claim within the ADR.",
                "maxLength": 64
              },
              "kind": {
                "type": "string",
                "description": "Claim kind.",
                "enum": [
                  "ProhibitedTechnology",
                  "RequiredTechnology",
                  "InfrastructureRequirement",
                  "DatabaseOwnership",
                  "IntegrationStyle",
                  "CloudProvider",
                  "AuthStrategy",
                  "DataResidency",
                  "DataRetention",
                  "Other"
                ]
              },
              "subject": {
                "type": "string",
                "description": "What the claim is about, e.g. \u0027Newtonsoft.Json\u0027, \u0027storage accounts\u0027, \u0027OrderService\u0027.",
                "maxLength": 200
              },
              "predicate": {
                "type": "string",
                "description": "Relation, e.g. \u0027must have\u0027, \u0027owns\u0027.",
                "maxLength": 100
              },
              "object": {
                "type": "string",
                "description": "Target value, e.g. \u0027private endpoint\u0027, \u0027OrdersDb\u0027, \u0027async\u0027.",
                "maxLength": 200
              },
              "scope": {
                "type": "object",
                "properties": {
                  "system": {
                    "type": "string",
                    "description": "System the claim applies to; omit for all.",
                    "maxLength": 100
                  },
                  "boundedContext": {
                    "type": "string",
                    "description": "Bounded context; omit for all.",
                    "maxLength": 100
                  },
                  "environment": {
                    "type": "string",
                    "description": "Environment such as production; omit for all.",
                    "maxLength": 50
                  },
                  "effectiveFrom": {
                    "type": "string",
                    "description": "ISO-8601 date-time."
                  },
                  "effectiveTo": {
                    "type": "string",
                    "description": "ISO-8601 date-time."
                  }
                },
                "additionalProperties": false
              },
              "sourceText": {
                "type": "string",
                "description": "Sentence in the decision this claim comes from.",
                "maxLength": 1000
              }
            },
            "additionalProperties": false,
            "required": [
              "claimId",
              "kind",
              "subject"
            ]
          },
          "description": "Structured, testable claims that drive drift and contradiction detection.",
          "maxItems": 200
        },
        "supersedes": {
          "type": "string",
          "description": "ADR id this one supersedes.",
          "maxLength": 128
        },
        "supersededBy": {
          "type": "string",
          "description": "ADR id that supersedes this one.",
          "maxLength": 128
        }
      }
    },
    "expectedEtag": {
      "type": "string",
      "description": "ETag from adr_get; the update fails with ETAG_MISMATCH if the ADR changed.",
      "maxLength": 200
    },
    "note": {
      "type": "string",
      "description": "Change note for the history.",
      "maxLength": 1000
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId",
    "fields"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "adr": {
          "type": "object",
          "properties": {
            "summary": {
              "type": "object",
              "properties": {
                "id": {
                  "type": "string",
                  "description": "ADR id."
                },
                "adrNumber": {
                  "type": "integer",
                  "description": "Sequence number within workspace."
                },
                "title": {
                  "type": "string",
                  "description": "Title."
                },
                "status": {
                  "type": "string",
                  "description": "Lifecycle status.",
                  "enum": [
                    "Draft",
                    "Proposed",
                    "UnderReview",
                    "Accepted",
                    "Rejected",
                    "Deprecated",
                    "Superseded",
                    "Archived"
                  ]
                },
                "tags": {
                  "type": "array",
                  "items": {
                    "type": "string",
                    "maxLength": 500
                  },
                  "description": "Tags.",
                  "maxItems": 50
                },
                "updatedAt": {
                  "type": "string",
                  "description": "ISO-8601."
                },
                "humanRevision": {
                  "type": "integer",
                  "description": "Human-owned revision."
                },
                "observedRevision": {
                  "type": "integer",
                  "description": "System-managed revision."
                },
                "driftStatus": {
                  "type": "string",
                  "description": "NotScanned, Compliant, Drifted, Uncertain, Unsupported or NoClaims."
                },
                "openContradictions": {
                  "type": "integer",
                  "description": "Open contradiction findings."
                },
                "openDriftFindings": {
                  "type": "integer",
                  "description": "Open drift findings."
                },
                "etag": {
                  "type": "string",
                  "description": "Concurrency token; pass back as expectedEtag on updates."
                }
              },
              "additionalProperties": false
            },
            "context": {
              "type": "string",
              "description": ""
            },
            "decision": {
              "type": "string",
              "description": ""
            },
            "rationale": {
              "type": "string",
              "description": ""
            },
            "alternatives": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "",
              "maxItems": 50
            },
            "consequences": {
              "type": "string",
              "description": ""
            },
            "owners": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "",
              "maxItems": 50
            },
            "approvers": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "",
              "maxItems": 50
            },
            "approvalResult": {
              "type": "string",
              "description": ""
            },
            "humanNotes": {
              "type": "string",
              "description": ""
            },
            "claims": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "claimId": {
                    "type": "string",
                    "description": "Stable id for the claim within the ADR.",
                    "maxLength": 64
                  },
                  "kind": {
                    "type": "string",
                    "description": "Claim kind.",
                    "enum": [
                      "ProhibitedTechnology",
                      "RequiredTechnology",
                      "InfrastructureRequirement",
                      "DatabaseOwnership",
                      "IntegrationStyle",
                      "CloudProvider",
                      "AuthStrategy",
                      "DataResidency",
                      "DataRetention",
                      "Other"
                    ]
                  },
                  "subject": {
                    "type": "string",
                    "description": "What the claim is about, e.g. \u0027Newtonsoft.Json\u0027, \u0027storage accounts\u0027, \u0027OrderService\u0027.",
                    "maxLength": 200
                  },
                  "predicate": {
                    "type": "string",
                    "description": "Relation, e.g. \u0027must have\u0027, \u0027owns\u0027.",
                    "maxLength": 100
                  },
                  "object": {
                    "type": "string",
                    "description": "Target value, e.g. \u0027private endpoint\u0027, \u0027OrdersDb\u0027, \u0027async\u0027.",
                    "maxLength": 200
                  },
                  "scope": {
                    "type": "object",
                    "properties": {
                      "system": {
                        "type": "string",
                        "description": "System the claim applies to; omit for all.",
                        "maxLength": 100
                      },
                      "boundedContext": {
                        "type": "string",
                        "description": "Bounded context; omit for all.",
                        "maxLength": 100
                      },
                      "environment": {
                        "type": "string",
                        "description": "Environment such as production; omit for all.",
                        "maxLength": 50
                      },
                      "effectiveFrom": {
                        "type": "string",
                        "description": "ISO-8601 date-time."
                      },
                      "effectiveTo": {
                        "type": "string",
                        "description": "ISO-8601 date-time."
                      }
                    },
                    "additionalProperties": false
                  },
                  "sourceText": {
                    "type": "string",
                    "description": "Sentence in the decision this claim comes from.",
                    "maxLength": 1000
                  }
                },
                "additionalProperties": false,
                "required": [
                  "claimId",
                  "kind",
                  "subject"
                ]
              },
              "description": ""
            },
            "supersedes": {
              "type": "string",
              "description": ""
            },
            "supersededBy": {
              "type": "string",
              "description": ""
            },
            "createdAt": {
              "type": "string",
              "description": ""
            },
            "acceptedAt": {
              "type": "string",
              "description": ""
            },
            "contentHash": {
              "type": "string",
              "description": "SHA-256 of canonical human-owned content."
            },
            "technologies": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": {
                    "type": "string",
                    "description": "Normalized name."
                  },
                  "version": {
                    "type": "string",
                    "description": ""
                  },
                  "category": {
                    "type": "string",
                    "description": ""
                  },
                  "status": {
                    "type": "string",
                    "description": "Observed, Inferred, Declared, Removed or Uncertain"
                  },
                  "confidence": {
                    "type": "number",
                    "description": ""
                  },
                  "basis": {
                    "type": "string",
                    "description": ""
                  },
                  "sourceFile": {
                    "type": "string",
                    "description": ""
                  },
                  "detector": {
                    "type": "string",
                    "description": "name@version"
                  },
                  "firstObservedAt": {
                    "type": "string",
                    "description": ""
                  },
                  "lastObservedAt": {
                    "type": "string",
                    "description": ""
                  }
                },
                "additionalProperties": false
              },
              "description": "Top observed technologies (system-managed)"
            },
            "technologyCount": {
              "type": "integer",
              "description": ""
            },
            "evidenceCount": {
              "type": "integer",
              "description": ""
            },
            "evidenceIds": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "",
              "maxItems": 50
            },
            "safetyFindingCount": {
              "type": "integer",
              "description": ""
            },
            "lastAnalyzedAt": {
              "type": "string",
              "description": ""
            },
            "markdown": {
              "type": "string",
              "description": "Only when requested."
            },
            "provenance": {
              "description": "Last human change provenance."
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `adr_propose_change`

**Purpose.** Propose changes to human-owned fields (title, context, decision, rationale, alternatives, consequences, status, owners, approvers, approvalResult, humanNotes, tags, claims, supersedes, supersededBy). Nothing is applied until a human approves via adr_approve_change. Use this for every analyzer- or assistant-suggested edit.

- Classification: **mutating**, phase **Mvp**, implemented
- Required permission: `AdrPropose`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `ADR_NOT_FOUND`, `INVALID_STATUS_TRANSITION`, `HUMAN_OWNED_FIELD_PROTECTED`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "changes": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "field": {
            "type": "string",
            "description": "Human-owned field name.",
            "maxLength": 50
          },
          "proposedValue": {
            "type": "string",
            "description": "New value. Lists may be JSON arrays or newline-separated; claims must be a JSON array.",
            "maxLength": 20000
          },
          "reason": {
            "type": "string",
            "description": "Why this change is proposed.",
            "maxLength": 2000
          }
        },
        "additionalProperties": false,
        "required": [
          "field",
          "proposedValue",
          "reason"
        ]
      },
      "description": "Field changes (1-15)",
      "maxItems": 15
    },
    "proposerType": {
      "type": "string",
      "description": "\u0027human\u0027 or \u0027agent\u0027. Agent proposals carry lower default confidence.",
      "enum": [
        "human",
        "agent"
      ]
    },
    "confidence": {
      "type": "number",
      "description": "Proposer confidence 0..1 (agent proposals are capped at 0.6).",
      "minimum": 0,
      "maximum": 1
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId",
    "changes"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "proposal": {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": ""
            },
            "adrId": {
              "type": "string",
              "description": ""
            },
            "status": {
              "type": "string",
              "description": "Proposed, Approved, Rejected, Applied, Expired, Withdrawn"
            },
            "baseHumanRevision": {
              "type": "integer",
              "description": ""
            },
            "proposedBy": {
              "type": "string",
              "description": ""
            },
            "proposerType": {
              "type": "string",
              "description": ""
            },
            "proposedAt": {
              "type": "string",
              "description": ""
            },
            "expiresAt": {
              "type": "string",
              "description": ""
            },
            "decidedBy": {
              "type": "string",
              "description": ""
            },
            "decidedAt": {
              "type": "string",
              "description": ""
            },
            "decisionNote": {
              "type": "string",
              "description": ""
            },
            "changes": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "field": {
                    "type": "string",
                    "description": ""
                  },
                  "proposedValuePreview": {
                    "type": "string",
                    "description": "First 300 chars."
                  },
                  "reason": {
                    "type": "string",
                    "description": ""
                  }
                },
                "additionalProperties": false
              },
              "description": ""
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "adrId": "adr-0007",
  "changes": [
    {
      "field": "consequences",
      "proposedValue": "Vendor dependency on Azure. Rollback: switch publishers to REST callbacks. Monitoring: dead-letter alerts in Application Insights.",
      "reason": "Safety review SR-SCL-001 flagged missing observability."
    }
  ],
  "proposerType": "agent",
  "confidence": 0.8,
  "idempotencyKey": "prop-0007-consequences-1"
}
```

**Example response (`data`)**

```json
{
  "proposal": {
    "id": "prop_9c\u2026",
    "adrId": "adr-0007",
    "status": "Proposed",
    "baseHumanRevision": 3,
    "proposedBy": "user:3f2a\u2026",
    "proposerType": "agent",
    "proposedAt": "\u2026",
    "expiresAt": "\u2026",
    "confidence": {
      "score": 0.6,
      "basis": "Inferred"
    },
    "changes": [
      {
        "field": "consequences",
        "proposedValuePreview": "Vendor dependency on Azure. Rollback: \u2026",
        "reason": "Safety review SR-SCL-001 flagged missing observability."
      }
    ]
  }
}
```

## `adr_approve_change`

**Purpose.** Approve and apply a pending proposal (Approver role). Enforces four-eyes (proposer cannot approve), stale-field protection, lifecycle transitions and ETag concurrency. Creates a new human revision with full provenance.

- Classification: **mutating**, phase **Mvp**, implemented
- Required permission: `AdrApproveChange`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `NOT_FOUND`, `CONFLICT`, `ETAG_MISMATCH`, `INVALID_STATUS_TRANSITION`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "proposalId": {
      "type": "string",
      "description": "Proposal id.",
      "maxLength": 128
    },
    "note": {
      "type": "string",
      "description": "Approval note.",
      "maxLength": 1000
    },
    "expectedAdrEtag": {
      "type": "string",
      "description": "ADR ETag for concurrency.",
      "maxLength": 200
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "proposalId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "proposal": {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": ""
            },
            "adrId": {
              "type": "string",
              "description": ""
            },
            "status": {
              "type": "string",
              "description": "Proposed, Approved, Rejected, Applied, Expired, Withdrawn"
            },
            "baseHumanRevision": {
              "type": "integer",
              "description": ""
            },
            "proposedBy": {
              "type": "string",
              "description": ""
            },
            "proposerType": {
              "type": "string",
              "description": ""
            },
            "proposedAt": {
              "type": "string",
              "description": ""
            },
            "expiresAt": {
              "type": "string",
              "description": ""
            },
            "decidedBy": {
              "type": "string",
              "description": ""
            },
            "decidedAt": {
              "type": "string",
              "description": ""
            },
            "decisionNote": {
              "type": "string",
              "description": ""
            },
            "changes": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "field": {
                    "type": "string",
                    "description": ""
                  },
                  "proposedValuePreview": {
                    "type": "string",
                    "description": "First 300 chars."
                  },
                  "reason": {
                    "type": "string",
                    "description": ""
                  }
                },
                "additionalProperties": false
              },
              "description": ""
            }
          },
          "additionalProperties": false
        },
        "adr": {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": "ADR id."
            },
            "adrNumber": {
              "type": "integer",
              "description": "Sequence number within workspace."
            },
            "title": {
              "type": "string",
              "description": "Title."
            },
            "status": {
              "type": "string",
              "description": "Lifecycle status.",
              "enum": [
                "Draft",
                "Proposed",
                "UnderReview",
                "Accepted",
                "Rejected",
                "Deprecated",
                "Superseded",
                "Archived"
              ]
            },
            "tags": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "Tags.",
              "maxItems": 50
            },
            "updatedAt": {
              "type": "string",
              "description": "ISO-8601."
            },
            "humanRevision": {
              "type": "integer",
              "description": "Human-owned revision."
            },
            "observedRevision": {
              "type": "integer",
              "description": "System-managed revision."
            },
            "driftStatus": {
              "type": "string",
              "description": "NotScanned, Compliant, Drifted, Uncertain, Unsupported or NoClaims."
            },
            "openContradictions": {
              "type": "integer",
              "description": "Open contradiction findings."
            },
            "openDriftFindings": {
              "type": "integer",
              "description": "Open drift findings."
            },
            "etag": {
              "type": "string",
              "description": "Concurrency token; pass back as expectedEtag on updates."
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "proposalId": "prop_9c\u2026",
  "note": "Agreed; observability section is accurate.",
  "idempotencyKey": "approve-prop_9c"
}
```

**Example response (`data`)**

```json
{
  "proposal": {
    "id": "prop_9c\u2026",
    "status": "Applied",
    "decidedBy": "user:77aa\u2026",
    "decidedAt": "\u2026",
    "decisionNote": "Agreed; observability section is accurate."
  },
  "adr": {
    "id": "adr-0007",
    "humanRevision": 4,
    "observedRevision": 4,
    "etag": "\u0022\u2026\u0022"
  }
}
```

## `adr_reject_change`

**Purpose.** Reject a pending proposal with a note (Approver role).

- Classification: **mutating**, phase **Extended**, implemented
- Required permission: `AdrApproveChange`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `NOT_FOUND`, `CONFLICT`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "proposalId": {
      "type": "string",
      "description": "Proposal id.",
      "maxLength": 128
    },
    "note": {
      "type": "string",
      "description": "Rejection note.",
      "maxLength": 1000
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "proposalId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "proposal": {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": ""
            },
            "adrId": {
              "type": "string",
              "description": ""
            },
            "status": {
              "type": "string",
              "description": "Proposed, Approved, Rejected, Applied, Expired, Withdrawn"
            },
            "baseHumanRevision": {
              "type": "integer",
              "description": ""
            },
            "proposedBy": {
              "type": "string",
              "description": ""
            },
            "proposerType": {
              "type": "string",
              "description": ""
            },
            "proposedAt": {
              "type": "string",
              "description": ""
            },
            "expiresAt": {
              "type": "string",
              "description": ""
            },
            "decidedBy": {
              "type": "string",
              "description": ""
            },
            "decidedAt": {
              "type": "string",
              "description": ""
            },
            "decisionNote": {
              "type": "string",
              "description": ""
            },
            "changes": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "field": {
                    "type": "string",
                    "description": ""
                  },
                  "proposedValuePreview": {
                    "type": "string",
                    "description": "First 300 chars."
                  },
                  "reason": {
                    "type": "string",
                    "description": ""
                  }
                },
                "additionalProperties": false
              },
              "description": ""
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `adr_supersede`

**Purpose.** Mark an ADR as Superseded by an Accepted ADR (human choice; Approver role). Both ADRs are updated and linked.

- Classification: **mutating**, phase **Extended**, implemented
- Required permission: `AdrSupersede`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `ADR_NOT_FOUND`, `INVALID_STATUS_TRANSITION`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "oldAdrId": {
      "type": "string",
      "description": "ADR being superseded.",
      "maxLength": 128
    },
    "newAdrId": {
      "type": "string",
      "description": "Accepted ADR that supersedes it.",
      "maxLength": 128
    },
    "note": {
      "type": "string",
      "description": "Note.",
      "maxLength": 1000
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "oldAdrId",
    "newAdrId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "old": {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": "ADR id."
            },
            "adrNumber": {
              "type": "integer",
              "description": "Sequence number within workspace."
            },
            "title": {
              "type": "string",
              "description": "Title."
            },
            "status": {
              "type": "string",
              "description": "Lifecycle status.",
              "enum": [
                "Draft",
                "Proposed",
                "UnderReview",
                "Accepted",
                "Rejected",
                "Deprecated",
                "Superseded",
                "Archived"
              ]
            },
            "tags": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "Tags.",
              "maxItems": 50
            },
            "updatedAt": {
              "type": "string",
              "description": "ISO-8601."
            },
            "humanRevision": {
              "type": "integer",
              "description": "Human-owned revision."
            },
            "observedRevision": {
              "type": "integer",
              "description": "System-managed revision."
            },
            "driftStatus": {
              "type": "string",
              "description": "NotScanned, Compliant, Drifted, Uncertain, Unsupported or NoClaims."
            },
            "openContradictions": {
              "type": "integer",
              "description": "Open contradiction findings."
            },
            "openDriftFindings": {
              "type": "integer",
              "description": "Open drift findings."
            },
            "etag": {
              "type": "string",
              "description": "Concurrency token; pass back as expectedEtag on updates."
            }
          },
          "additionalProperties": false
        },
        "new": {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": "ADR id."
            },
            "adrNumber": {
              "type": "integer",
              "description": "Sequence number within workspace."
            },
            "title": {
              "type": "string",
              "description": "Title."
            },
            "status": {
              "type": "string",
              "description": "Lifecycle status.",
              "enum": [
                "Draft",
                "Proposed",
                "UnderReview",
                "Accepted",
                "Rejected",
                "Deprecated",
                "Superseded",
                "Archived"
              ]
            },
            "tags": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "Tags.",
              "maxItems": 50
            },
            "updatedAt": {
              "type": "string",
              "description": "ISO-8601."
            },
            "humanRevision": {
              "type": "integer",
              "description": "Human-owned revision."
            },
            "observedRevision": {
              "type": "integer",
              "description": "System-managed revision."
            },
            "driftStatus": {
              "type": "string",
              "description": "NotScanned, Compliant, Drifted, Uncertain, Unsupported or NoClaims."
            },
            "openContradictions": {
              "type": "integer",
              "description": "Open contradiction findings."
            },
            "openDriftFindings": {
              "type": "integer",
              "description": "Open drift findings."
            },
            "etag": {
              "type": "string",
              "description": "Concurrency token; pass back as expectedEtag on updates."
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `adr_archive`

**Purpose.** Archive an ADR with a reason (Approver role). Archived ADRs are read-only and excluded from analysis.

- Classification: **mutating**, phase **Extended**, implemented
- Required permission: `AdrArchive`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `ADR_NOT_FOUND`, `INVALID_STATUS_TRANSITION`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "reason": {
      "type": "string",
      "description": "Why it is archived.",
      "maxLength": 1000
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId",
    "reason"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "adr": {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": "ADR id."
            },
            "adrNumber": {
              "type": "integer",
              "description": "Sequence number within workspace."
            },
            "title": {
              "type": "string",
              "description": "Title."
            },
            "status": {
              "type": "string",
              "description": "Lifecycle status.",
              "enum": [
                "Draft",
                "Proposed",
                "UnderReview",
                "Accepted",
                "Rejected",
                "Deprecated",
                "Superseded",
                "Archived"
              ]
            },
            "tags": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "Tags.",
              "maxItems": 50
            },
            "updatedAt": {
              "type": "string",
              "description": "ISO-8601."
            },
            "humanRevision": {
              "type": "integer",
              "description": "Human-owned revision."
            },
            "observedRevision": {
              "type": "integer",
              "description": "System-managed revision."
            },
            "driftStatus": {
              "type": "string",
              "description": "NotScanned, Compliant, Drifted, Uncertain, Unsupported or NoClaims."
            },
            "openContradictions": {
              "type": "integer",
              "description": "Open contradiction findings."
            },
            "openDriftFindings": {
              "type": "integer",
              "description": "Open drift findings."
            },
            "etag": {
              "type": "string",
              "description": "Concurrency token; pass back as expectedEtag on updates."
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `adr_history`

**Purpose.** List version snapshots (human and observed revisions) with provenance and content hashes, newest first.

- Classification: **read-only**, phase **Extended**, implemented
- Required permission: `AdrRead`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `ADR_NOT_FOUND`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "take": {
      "type": "integer",
      "description": "Max snapshots.",
      "minimum": 1,
      "maximum": 100,
      "default": 20
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "versions": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "id": {
                "type": "string",
                "description": ""
              },
              "humanRevision": {
                "type": "integer",
                "description": ""
              },
              "observedRevision": {
                "type": "integer",
                "description": ""
              },
              "changeKind": {
                "type": "string",
                "description": ""
              },
              "capturedAt": {
                "type": "string",
                "description": ""
              },
              "actor": {
                "type": "string",
                "description": ""
              },
              "actorType": {
                "type": "string",
                "description": ""
              },
              "contentHash": {
                "type": "string",
                "description": ""
              },
              "summary": {
                "type": "string",
                "description": ""
              }
            },
            "additionalProperties": false
          },
          "description": "Snapshots"
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `adr_import`

**Purpose.** Load existing decisions (up to 25 per call) in the lifecycle status they already have, keeping their ids and original decision dates. Approver role. Human-owned content is stored verbatim with the original Markdown as source of record. Never overwrites: an existing id is reported as unchanged or conflict. Use dryRun to validate first.

- Classification: **mutating**, phase **Extended**, implemented
- Required permission: `AdrImport`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `PAYLOAD_TOO_LARGE`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "records": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "id": {
            "type": "string",
            "description": "Id to keep, e.g. ADR-204001.",
            "maxLength": 128,
            "pattern": "^[A-Za-z0-9._:-]\u002B$"
          },
          "markdown": {
            "type": "string",
            "description": "Original ADR Markdown (\u003C= 200,000 chars).",
            "maxLength": 200000
          },
          "fields": {
            "type": "object",
            "description": "Structured fields applied over the parsed Markdown (status, tags, notes, supersession).",
            "additionalProperties": false,
            "properties": {
              "title": {
                "type": "string",
                "description": "Title.",
                "maxLength": 300
              },
              "context": {
                "type": "string",
                "description": "Context / problem statement.",
                "maxLength": 20000
              },
              "decision": {
                "type": "string",
                "description": "The decision.",
                "maxLength": 20000
              },
              "rationale": {
                "type": "string",
                "description": "Why.",
                "maxLength": 20000
              },
              "alternatives": {
                "type": "array",
                "items": {
                  "type": "string",
                  "maxLength": 500
                },
                "description": "Considered alternatives.",
                "maxItems": 50
              },
              "consequences": {
                "type": "string",
                "description": "Consequences.",
                "maxLength": 20000
              },
              "status": {
                "type": "string",
                "description": "Lifecycle status.",
                "enum": [
                  "Draft",
                  "Proposed",
                  "UnderReview",
                  "Accepted",
                  "Rejected",
                  "Deprecated",
                  "Superseded",
                  "Archived"
                ]
              },
              "owners": {
                "type": "array",
                "items": {
                  "type": "string",
                  "maxLength": 500
                },
                "description": "Owners.",
                "maxItems": 50
              },
              "approvers": {
                "type": "array",
                "items": {
                  "type": "string",
                  "maxLength": 500
                },
                "description": "Approvers.",
                "maxItems": 50
              },
              "approvalResult": {
                "type": "string",
                "description": "Approval outcome text.",
                "maxLength": 2000
              },
              "humanNotes": {
                "type": "string",
                "description": "Free notes.",
                "maxLength": 20000
              },
              "tags": {
                "type": "array",
                "items": {
                  "type": "string",
                  "maxLength": 500
                },
                "description": "Tags.",
                "maxItems": 50
              },
              "claims": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "claimId": {
                      "type": "string",
                      "description": "Stable id for the claim within the ADR.",
                      "maxLength": 64
                    },
                    "kind": {
                      "type": "string",
                      "description": "Claim kind.",
                      "enum": [
                        "ProhibitedTechnology",
                        "RequiredTechnology",
                        "InfrastructureRequirement",
                        "DatabaseOwnership",
                        "IntegrationStyle",
                        "CloudProvider",
                        "AuthStrategy",
                        "DataResidency",
                        "DataRetention",
                        "Other"
                      ]
                    },
                    "subject": {
                      "type": "string",
                      "description": "What the claim is about, e.g. \u0027Newtonsoft.Json\u0027, \u0027storage accounts\u0027, \u0027OrderService\u0027.",
                      "maxLength": 200
                    },
                    "predicate": {
                      "type": "string",
                      "description": "Relation, e.g. \u0027must have\u0027, \u0027owns\u0027.",
                      "maxLength": 100
                    },
                    "object": {
                      "type": "string",
                      "description": "Target value, e.g. \u0027private endpoint\u0027, \u0027OrdersDb\u0027, \u0027async\u0027.",
                      "maxLength": 200
                    },
                    "scope": {
                      "type": "object",
                      "properties": {
                        "system": {
                          "type": "string",
                          "description": "System the claim applies to; omit for all.",
                          "maxLength": 100
                        },
                        "boundedContext": {
                          "type": "string",
                          "description": "Bounded context; omit for all.",
                          "maxLength": 100
                        },
                        "environment": {
                          "type": "string",
                          "description": "Environment such as production; omit for all.",
                          "maxLength": 50
                        },
                        "effectiveFrom": {
                          "type": "string",
                          "description": "ISO-8601 date-time."
                        },
                        "effectiveTo": {
                          "type": "string",
                          "description": "ISO-8601 date-time."
                        }
                      },
                      "additionalProperties": false
                    },
                    "sourceText": {
                      "type": "string",
                      "description": "Sentence in the decision this claim comes from.",
                      "maxLength": 1000
                    }
                  },
                  "additionalProperties": false,
                  "required": [
                    "claimId",
                    "kind",
                    "subject"
                  ]
                },
                "description": "Structured, testable claims that drive drift and contradiction detection.",
                "maxItems": 200
              },
              "supersedes": {
                "type": "string",
                "description": "ADR id this one supersedes.",
                "maxLength": 128
              },
              "supersededBy": {
                "type": "string",
                "description": "ADR id that supersedes this one.",
                "maxLength": 128
              }
            }
          },
          "decidedOn": {
            "type": "string",
            "description": "Original decision date, yyyy-MM-dd. Becomes the created date.",
            "maxLength": 10,
            "pattern": "^\\d{4}-\\d{2}-\\d{2}$"
          },
          "sourcePath": {
            "type": "string",
            "description": "Where the document came from; recorded in the history.",
            "maxLength": 500
          }
        },
        "additionalProperties": false,
        "required": [
          "id"
        ]
      },
      "description": "Decisions to load (1-25)",
      "maxItems": 25
    },
    "dryRun": {
      "type": "boolean",
      "description": "Validate and report what would happen without writing.",
      "default": false
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "records"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "dryRun": {
          "type": "boolean",
          "description": ""
        },
        "counts": {
          "description": "Number of records per outcome."
        },
        "results": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "id": {
                "type": "string",
                "description": ""
              },
              "outcome": {
                "type": "string",
                "description": "created, would-create, unchanged, conflict or invalid"
              },
              "message": {
                "type": "string",
                "description": ""
              }
            },
            "additionalProperties": false
          },
          "description": "One result per record"
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `adr_export`

**Purpose.** Export an ADR as Markdown or JSON.

- Classification: **read-only**, phase **Extended**, implemented
- Required permission: `AdrRead`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `ADR_NOT_FOUND`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "format": {
      "type": "string",
      "description": "markdown (default) or json.",
      "enum": [
        "markdown",
        "json"
      ]
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "format": {
          "type": "string",
          "description": ""
        },
        "content": {
          "type": "string",
          "description": "Exported content."
        },
        "contentHash": {
          "type": "string",
          "description": ""
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `file_upload_request`

**Purpose.** Start a fast direct-to-Blob upload. Validates name, type, size and policy, then returns a short-lived, write-only, single-object upload URL. The file does NOT pass through MCP. Follow with file_register_upload.

- Classification: **mutating**, phase **Mvp**, implemented
- Required permission: `EvidenceUpload`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `UNSUPPORTED_FILE_TYPE`, `PAYLOAD_TOO_LARGE`, `CONFLICT`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "fileName": {
      "type": "string",
      "description": "Original file name (path segments are stripped).",
      "maxLength": 255
    },
    "mediaType": {
      "type": "string",
      "description": "MIME type, e.g. text/plain, application/json, application/zip.",
      "maxLength": 120
    },
    "expectedSizeBytes": {
      "type": "integer",
      "description": "Expected size in bytes (\u003C= 50 MiB by default).",
      "minimum": 1
    },
    "sha256": {
      "type": "string",
      "description": "Optional lower-case hex SHA-256 of the content for verification and duplicate detection.",
      "maxLength": 64,
      "pattern": "^[a-fA-F0-9]{64}$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "note": {
      "type": "string",
      "description": "Why this file is evidence.",
      "maxLength": 1000
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "fileName",
    "mediaType",
    "expectedSizeBytes"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "uploadId": {
          "type": "string",
          "description": "Use in file_register_upload."
        },
        "uploadUrl": {
          "type": "string",
          "description": "Pre-authorized URL. Write-only, single blob, expires at expiresAt. Never log or share."
        },
        "method": {
          "type": "string",
          "description": "HTTP method (PUT)."
        },
        "headers": {
          "description": "Headers that must be sent with the PUT."
        },
        "expiresAt": {
          "type": "string",
          "description": "ISO-8601 expiry."
        },
        "maxBytes": {
          "type": "integer",
          "description": "Hard size limit."
        },
        "instructions": {
          "type": "string",
          "description": "Human-readable steps."
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "fileName": "src/Orders/Orders.csproj",
  "mediaType": "application/xml",
  "expectedSizeBytes": 1842,
  "sha256": "3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
  "adrId": "adr-0007"
}
```

**Example response (`data`)**

```json
{
  "uploadId": "upl_5e\u2026",
  "uploadUrl": "https://stadr\u2026.blob.core.windows.net/uploads-quarantine/tenant/default/upl_5e\u2026/Orders.csproj?sv=\u2026\u0026sp=cw\u0026se=\u2026\u0026sig=\u2026",
  "method": "PUT",
  "headers": {
    "x-ms-blob-type": "BlockBlob",
    "Content-Type": "application/xml",
    "x-ms-version": "2024-11-04"
  },
  "expiresAt": "2026-10-07T04:25:03Z",
  "maxBytes": 1842,
  "instructions": "PUT the raw file bytes to uploadUrl with the listed headers before 2026-10-07T04:25:03Z, then call file_register_upload with uploadId=\u0027upl_5e\u2026\u0027."
}
```

## `file_register_upload`

**Purpose.** Finish an upload: verifies blob existence, size, SHA-256 and media type; quarantines; scans for secrets/PII/prompt-injection; writes a sanitized variant; promotes to immutable evidence; optionally attaches to an ADR; queues technology detection and contradiction analysis. Safe to repeat (returns the recorded outcome).

- Classification: **mutating**, phase **Mvp**, implemented
- Required permission: `EvidenceUpload`
- Idempotency: Natural
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `UPLOAD_NOT_FOUND`, `UPLOAD_EXPIRED`, `CHECKSUM_MISMATCH`, `PAYLOAD_TOO_LARGE`, `UNSUPPORTED_FILE_TYPE`, `CONTENT_SAFETY_BLOCKED`, `UPLOAD_REJECTED`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "uploadId": {
      "type": "string",
      "description": "From file_upload_request.",
      "maxLength": 128
    },
    "sha256": {
      "type": "string",
      "description": "SHA-256 of what was uploaded, if not supplied earlier.",
      "maxLength": 64
    }
  },
  "additionalProperties": false,
  "required": [
    "uploadId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "evidenceId": {
          "type": "string",
          "description": "Equals uploadId once approved."
        },
        "state": {
          "type": "string",
          "description": "Requested, Uploaded, Quarantined, Scanning, Approved, Rejected, Expired, Removed or Duplicate"
        },
        "sha256": {
          "type": "string",
          "description": ""
        },
        "sizeBytes": {
          "type": "integer",
          "description": ""
        },
        "mediaType": {
          "type": "string",
          "description": ""
        },
        "safety": {
          "type": "object",
          "properties": {
            "detections": {
              "type": "integer",
              "description": "Count of masked/flagged items."
            },
            "bySeverity": {
              "description": "Counts by severity."
            },
            "blocked": {
              "type": "boolean",
              "description": ""
            },
            "promptInjectionSuspected": {
              "type": "boolean",
              "description": ""
            },
            "limitations": {
              "type": "array",
              "items": {
                "type": "string",
                "maxLength": 500
              },
              "description": "Scanner limitations.",
              "maxItems": 50
            }
          },
          "additionalProperties": false
        },
        "duplicateOfEvidenceId": {
          "type": "string",
          "description": ""
        },
        "jobId": {
          "type": "string",
          "description": "Async analysis job."
        },
        "replayed": {
          "type": "boolean",
          "description": "True when this upload had already been registered."
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "uploadId": "upl_5e\u2026"
}
```

**Example response (`data`)**

```json
{
  "evidenceId": "upl_5e\u2026",
  "state": "Approved",
  "sha256": "3b0c\u2026",
  "sizeBytes": 1842,
  "mediaType": "application/xml",
  "safety": {
    "detections": 0,
    "bySeverity": {},
    "blocked": false,
    "promptInjectionSuspected": false,
    "limitations": [
      "Regex-based detection: \u2026"
    ]
  },
  "jobId": "job_1a\u2026",
  "replayed": false
}
```

## `file_import_from_repository`

**Purpose.** Import files from an allow-listed Git host/organization as evidence (asynchronous job). Repository hosts and organizations are configured server-side; arbitrary URLs are rejected.

- Classification: **mutating**, phase **Later**, **specified only (not exposed by the host until implemented)**
- Required permission: `EvidenceUpload`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `POLICY_BLOCKED`, `NOT_IMPLEMENTED`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "repositoryUrl": {
      "type": "string",
      "description": "https URL of an allow-listed repository.",
      "maxLength": 500
    },
    "ref": {
      "type": "string",
      "description": "Branch, tag or commit.",
      "maxLength": 200
    },
    "pathGlobs": {
      "type": "array",
      "items": {
        "type": "string",
        "maxLength": 500
      },
      "description": "Globs to include.",
      "maxItems": 20
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "repositoryUrl"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "job": {
          "type": "object",
          "properties": {
            "jobId": {
              "type": "string",
              "description": "Job id."
            },
            "type": {
              "type": "string",
              "description": "Job type."
            },
            "state": {
              "type": "string",
              "description": "Queued, Running, Succeeded, Failed, Cancelled, Expired, Poisoned."
            },
            "progressPercent": {
              "type": "integer",
              "description": "0..100"
            },
            "statusMessage": {
              "type": "string",
              "description": ""
            },
            "createdAt": {
              "type": "string",
              "description": ""
            },
            "completedAt": {
              "type": "string",
              "description": ""
            },
            "errorCode": {
              "type": "string",
              "description": ""
            },
            "expiresAt": {
              "type": "string",
              "description": "When the result is purged."
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `evidence_attach`

**Purpose.** Attach approved evidence to an ADR (system-managed evidence list, human action with provenance).

- Classification: **mutating**, phase **Mvp**, implemented
- Required permission: `EvidenceUpload`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `ADR_NOT_FOUND`, `NOT_FOUND`, `CONFLICT`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "evidenceId": {
      "type": "string",
      "description": "Approved evidence id.",
      "maxLength": 128
    },
    "note": {
      "type": "string",
      "description": "Why it is relevant.",
      "maxLength": 1000
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId",
    "evidenceId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "adrId": {
          "type": "string",
          "description": ""
        },
        "evidenceCount": {
          "type": "integer",
          "description": ""
        },
        "evidence": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "evidenceId": {
                "type": "string",
                "description": ""
              },
              "fileName": {
                "type": "string",
                "description": ""
              },
              "mediaType": {
                "type": "string",
                "description": ""
              },
              "sizeBytes": {
                "type": "integer",
                "description": ""
              },
              "sha256": {
                "type": "string",
                "description": ""
              },
              "attachedAt": {
                "type": "string",
                "description": ""
              }
            },
            "additionalProperties": false
          },
          "description": "Attached evidence"
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "adrId": "adr-0008",
  "evidenceId": "upl_5e\u2026",
  "note": "Shared manifest also supports this decision."
}
```

**Example response (`data`)**

```json
{
  "adrId": "adr-0008",
  "evidenceCount": 1,
  "evidence": [
    {
      "evidenceId": "upl_5e\u2026",
      "fileName": "Orders.csproj",
      "mediaType": "application/xml",
      "sizeBytes": 1842,
      "sha256": "3b0c\u2026",
      "attachedAt": "\u2026"
    }
  ]
}
```

## `evidence_list`

**Purpose.** List approved evidence in the workspace, optionally only for one ADR. Metadata only; never returns file contents.

- Classification: **read-only**, phase **Extended**, implemented
- Required permission: `AdrRead`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    }
  },
  "additionalProperties": false
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "items": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "evidenceId": {
                "type": "string",
                "description": ""
              },
              "fileName": {
                "type": "string",
                "description": ""
              },
              "mediaType": {
                "type": "string",
                "description": ""
              },
              "sizeBytes": {
                "type": "integer",
                "description": ""
              },
              "sha256": {
                "type": "string",
                "description": ""
              },
              "adrId": {
                "type": "string",
                "description": ""
              },
              "state": {
                "type": "string",
                "description": ""
              },
              "safetyDetections": {
                "type": "integer",
                "description": ""
              },
              "registeredAt": {
                "type": "string",
                "description": ""
              }
            },
            "additionalProperties": false
          },
          "description": "Evidence"
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `evidence_remove`

**Purpose.** Detach evidence from an ADR with a reason (Approver role). The immutable evidence blob is retained for audit; only the link is removed.

- Classification: **mutating**, phase **Extended**, implemented
- Required permission: `EvidenceRemove`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `ADR_NOT_FOUND`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "evidenceId": {
      "type": "string",
      "description": "",
      "maxLength": 128
    },
    "reason": {
      "type": "string",
      "description": "",
      "maxLength": 1000
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId",
    "evidenceId",
    "reason"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "adrId": {
          "type": "string",
          "description": ""
        },
        "evidenceCount": {
          "type": "integer",
          "description": ""
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `technology_detect`

**Purpose.** Deterministically detect technologies and versions from inline files (<= 50, <= 512 KB each) or from the workspace's approved evidence. Nothing is persisted. Each result carries detector, confidence and evidence file.

- Classification: **read-only**, phase **Mvp**, implemented
- Required permission: `AnalysisRun`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `PAYLOAD_TOO_LARGE`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "files": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "path": {
            "type": "string",
            "description": "Relative path (used to pick parsers).",
            "maxLength": 400
          },
          "content": {
            "type": "string",
            "description": "File content.",
            "maxLength": 524288
          }
        },
        "additionalProperties": false,
        "required": [
          "path",
          "content"
        ]
      },
      "description": "Inline files",
      "maxItems": 50
    }
  },
  "additionalProperties": false
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "technologies": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "name": {
                "type": "string",
                "description": "Normalized name."
              },
              "version": {
                "type": "string",
                "description": ""
              },
              "category": {
                "type": "string",
                "description": ""
              },
              "status": {
                "type": "string",
                "description": "Observed, Inferred, Declared, Removed or Uncertain"
              },
              "confidence": {
                "type": "number",
                "description": ""
              },
              "basis": {
                "type": "string",
                "description": ""
              },
              "sourceFile": {
                "type": "string",
                "description": ""
              },
              "detector": {
                "type": "string",
                "description": "name@version"
              },
              "firstObservedAt": {
                "type": "string",
                "description": ""
              },
              "lastObservedAt": {
                "type": "string",
                "description": ""
              }
            },
            "additionalProperties": false
          },
          "description": "Detected technologies"
        },
        "detectors": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Detectors that ran",
          "maxItems": 50
        },
        "filesScanned": {
          "type": "integer",
          "description": ""
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "files": [
    {
      "path": "web/package.json",
      "content": "{\u0022dependencies\u0022:{\u0022express\u0022:\u0022^4.19.2\u0022,\u0022@azure/cosmos\u0022:\u00224.1.0\u0022}}"
    }
  ]
}
```

**Example response (`data`)**

```json
{
  "technologies": [
    {
      "name": "Azure Cosmos DB SDK",
      "version": "4.1.0",
      "category": "Database",
      "status": "Observed",
      "confidence": 0.95,
      "basis": "Observed",
      "sourceFile": "web/package.json",
      "detector": "package.json@1.0.0"
    },
    {
      "name": "Express",
      "version": "4.19.2",
      "category": "Framework",
      "status": "Observed",
      "confidence": 0.95,
      "basis": "Observed",
      "sourceFile": "web/package.json",
      "detector": "package.json@1.0.0"
    },
    {
      "name": "Node.js",
      "category": "Runtime",
      "status": "Observed",
      "confidence": 0.9,
      "basis": "Observed",
      "sourceFile": "web/package.json",
      "detector": "package.json@1.0.0"
    }
  ],
  "detectors": [
    "csproj@1.0.0",
    "package.json@1.0.0",
    "python-requirements@1.0.0",
    "dockerfile@1.0.0",
    "bicep@1.0.0",
    "terraform@1.0.0",
    "kubernetes@1.0.0",
    "cicd@1.0.0"
  ],
  "filesScanned": 1
}
```

## `technology_refresh`

**Purpose.** Refresh the ADR's system-managed observed technologies from workspace evidence and run drift detection. Preserves first-seen timestamps, marks vanished items Removed, never edits the decision. Set async=true for large workspaces to get a job id.

- Classification: **mutating**, phase **Mvp**, implemented
- Required permission: `AnalysisRun`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `ADR_NOT_FOUND`, `ETAG_MISMATCH`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "async": {
      "type": "boolean",
      "description": "Run as a background job.",
      "default": false
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "adrId": {
          "type": "string",
          "description": ""
        },
        "added": {
          "type": "integer",
          "description": ""
        },
        "updated": {
          "type": "integer",
          "description": ""
        },
        "removed": {
          "type": "integer",
          "description": ""
        },
        "technologies": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "name": {
                "type": "string",
                "description": "Normalized name."
              },
              "version": {
                "type": "string",
                "description": ""
              },
              "category": {
                "type": "string",
                "description": ""
              },
              "status": {
                "type": "string",
                "description": "Observed, Inferred, Declared, Removed or Uncertain"
              },
              "confidence": {
                "type": "number",
                "description": ""
              },
              "basis": {
                "type": "string",
                "description": ""
              },
              "sourceFile": {
                "type": "string",
                "description": ""
              },
              "detector": {
                "type": "string",
                "description": "name@version"
              },
              "firstObservedAt": {
                "type": "string",
                "description": ""
              },
              "lastObservedAt": {
                "type": "string",
                "description": ""
              }
            },
            "additionalProperties": false
          },
          "description": "Current observed technologies"
        },
        "drift": {
          "type": "object",
          "properties": {
            "overallStatus": {
              "type": "string",
              "description": ""
            },
            "findings": {
              "type": "integer",
              "description": ""
            },
            "statusChanges": {
              "type": "integer",
              "description": ""
            }
          },
          "additionalProperties": false
        },
        "job": {
          "type": "object",
          "properties": {
            "jobId": {
              "type": "string",
              "description": "Job id."
            },
            "type": {
              "type": "string",
              "description": "Job type."
            },
            "state": {
              "type": "string",
              "description": "Queued, Running, Succeeded, Failed, Cancelled, Expired, Poisoned."
            },
            "progressPercent": {
              "type": "integer",
              "description": "0..100"
            },
            "statusMessage": {
              "type": "string",
              "description": ""
            },
            "createdAt": {
              "type": "string",
              "description": ""
            },
            "completedAt": {
              "type": "string",
              "description": ""
            },
            "errorCode": {
              "type": "string",
              "description": ""
            },
            "expiresAt": {
              "type": "string",
              "description": "When the result is purged."
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "adrId": "adr-0007"
}
```

**Example response (`data`)**

```json
{
  "adrId": "adr-0007",
  "added": 3,
  "updated": 0,
  "removed": 1,
  "technologies": [
    {
      "name": "Azure Service Bus",
      "version": "7.18.2",
      "category": "Messaging",
      "status": "Observed",
      "confidence": 0.95,
      "basis": "Observed",
      "sourceFile": "src/Orders/Orders.csproj",
      "detector": "csproj@1.0.0"
    }
  ],
  "drift": {
    "overallStatus": "Compliant",
    "findings": 2,
    "statusChanges": 0
  }
}
```

## `adr_find_related_code`

**Purpose.** Find code and infrastructure files related to an ADR via keyword and claim matching over workspace evidence.

- Classification: **read-only**, phase **Later**, **specified only (not exposed by the host until implemented)**
- Required permission: `AdrRead`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `NOT_IMPLEMENTED`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "take": {
      "type": "integer",
      "description": "",
      "minimum": 1,
      "maximum": 100,
      "default": 20
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "files": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "path": {
                "type": "string",
                "description": ""
              },
              "relevance": {
                "type": "number",
                "description": ""
              },
              "reason": {
                "type": "string",
                "description": ""
              }
            },
            "additionalProperties": false
          },
          "description": ""
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `adr_detect_drift`

**Purpose.** Evaluate an accepted ADR's declared and extracted claims against workspace evidence using detector plugins. Each claim is Compliant, Drifted, Uncertain, Unsupported or NotApplicable with evidence and confidence; prior results are preserved and status changes are flagged. The decision text is never altered.

- Classification: **mutating**, phase **Mvp**, implemented
- Required permission: `AnalysisRun`
- Idempotency: Natural
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `ADR_NOT_FOUND`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "async": {
      "type": "boolean",
      "description": "Run as a background job.",
      "default": false
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "adrId": {
          "type": "string",
          "description": ""
        },
        "overallStatus": {
          "type": "string",
          "description": ""
        },
        "statusChanges": {
          "type": "integer",
          "description": ""
        },
        "evidenceFiles": {
          "type": "integer",
          "description": ""
        },
        "detectors": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "",
          "maxItems": 50
        },
        "findings": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "id": {
                "type": "string",
                "description": "Finding id."
              },
              "kind": {
                "type": "string",
                "description": "Contradiction, Drift, Safety or Governance."
              },
              "severity": {
                "type": "string",
                "description": "Info..Critical."
              },
              "confidence": {
                "type": "object",
                "properties": {
                  "score": {
                    "type": "number",
                    "description": "0..1"
                  },
                  "basis": {
                    "type": "string",
                    "description": "Observed, Declared, Inferred or Uncertain."
                  },
                  "rationale": {
                    "type": "string",
                    "description": "Why."
                  }
                },
                "additionalProperties": false
              },
              "summary": {
                "type": "string",
                "description": "One-line summary."
              },
              "explanation": {
                "type": "string",
                "description": "Reasoning with evidence references."
              },
              "reviewStatus": {
                "type": "string",
                "description": "Open, Confirmed, AcceptableException, FalsePositive, Resolved."
              },
              "requiresHumanReview": {
                "type": "boolean",
                "description": "Whether a human must classify this finding."
              },
              "evidence": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "evidenceId": {
                      "type": "string",
                      "description": ""
                    },
                    "sourcePath": {
                      "type": "string",
                      "description": ""
                    },
                    "excerpt": {
                      "type": "string",
                      "description": "Sanitized, truncated excerpt."
                    },
                    "lineNumber": {
                      "type": "integer",
                      "description": ""
                    }
                  },
                  "additionalProperties": false
                },
                "description": "Supporting evidence"
              },
              "claimId": {
                "type": "string",
                "description": ""
              },
              "claimText": {
                "type": "string",
                "description": ""
              },
              "result": {
                "type": "string",
                "description": "Compliant, Drifted, Uncertain, Unsupported, NotApplicable"
              },
              "previousResult": {
                "type": "string",
                "description": ""
              },
              "statusChanged": {
                "type": "boolean",
                "description": ""
              },
              "detector": {
                "type": "string",
                "description": ""
              }
            },
            "additionalProperties": false
          },
          "description": "Per-claim results"
        },
        "job": {
          "type": "object",
          "properties": {
            "jobId": {
              "type": "string",
              "description": "Job id."
            },
            "type": {
              "type": "string",
              "description": "Job type."
            },
            "state": {
              "type": "string",
              "description": "Queued, Running, Succeeded, Failed, Cancelled, Expired, Poisoned."
            },
            "progressPercent": {
              "type": "integer",
              "description": "0..100"
            },
            "statusMessage": {
              "type": "string",
              "description": ""
            },
            "createdAt": {
              "type": "string",
              "description": ""
            },
            "completedAt": {
              "type": "string",
              "description": ""
            },
            "errorCode": {
              "type": "string",
              "description": ""
            },
            "expiresAt": {
              "type": "string",
              "description": "When the result is purged."
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "adrId": "adr-0003"
}
```

**Example response (`data`)**

```json
{
  "adrId": "adr-0003",
  "overallStatus": "Drifted",
  "statusChanges": 1,
  "evidenceFiles": 14,
  "detectors": [
    "prohibited-dependency@1.0.0",
    "infrastructure-requirement@1.0.0",
    "database-ownership@1.0.0"
  ],
  "findings": [
    {
      "id": "drf_\u2026",
      "kind": "Drift",
      "severity": "High",
      "confidence": {
        "score": 0.9,
        "basis": "Observed",
        "rationale": "Exact token match in dependency manifests/imports."
      },
      "summary": "Drifted: must not use Newtonsoft.Json",
      "explanation": "\u0027Newtonsoft.Json\u0027 is prohibited but referenced in 1 file(s).",
      "reviewStatus": "Open",
      "requiresHumanReview": true,
      "claimId": "c1",
      "claimText": "must not use Newtonsoft.Json",
      "result": "Drifted",
      "previousResult": "Compliant",
      "statusChanged": true,
      "detector": "prohibited-dependency@1.0.0",
      "evidence": [
        {
          "evidenceId": "upl_\u2026",
          "sourcePath": "src/Api/Api.csproj",
          "excerpt": "\u003CPackageReference Include=\u0022Newtonsoft.Json\u0022 Version=\u002213.0.3\u0022 /\u003E",
          "lineNumber": 12
        }
      ]
    }
  ]
}
```

## `adr_check_contradictions`

**Purpose.** Hybrid contradiction check between an ADR (or all active ADRs) and other active/draft ADRs: normalize claims, deterministic candidate matching, scope/date/status filtering, optional semantic pass, explained findings with confidence and severity. Humans classify results with finding_review; ADR status is never changed.

- Classification: **mutating**, phase **Mvp**, implemented
- Required permission: `AnalysisRun`
- Idempotency: Natural
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `ADR_NOT_FOUND`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "includeDrafts": {
      "type": "boolean",
      "description": "Compare against Draft/Proposed/UnderReview ADRs too.",
      "default": true
    },
    "persist": {
      "type": "boolean",
      "description": "Persist findings and link them to ADRs (default true).",
      "default": true
    }
  },
  "additionalProperties": false
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "findings": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "id": {
                "type": "string",
                "description": "Finding id."
              },
              "kind": {
                "type": "string",
                "description": "Contradiction, Drift, Safety or Governance."
              },
              "severity": {
                "type": "string",
                "description": "Info..Critical."
              },
              "confidence": {
                "type": "object",
                "properties": {
                  "score": {
                    "type": "number",
                    "description": "0..1"
                  },
                  "basis": {
                    "type": "string",
                    "description": "Observed, Declared, Inferred or Uncertain."
                  },
                  "rationale": {
                    "type": "string",
                    "description": "Why."
                  }
                },
                "additionalProperties": false
              },
              "summary": {
                "type": "string",
                "description": "One-line summary."
              },
              "explanation": {
                "type": "string",
                "description": "Reasoning with evidence references."
              },
              "reviewStatus": {
                "type": "string",
                "description": "Open, Confirmed, AcceptableException, FalsePositive, Resolved."
              },
              "requiresHumanReview": {
                "type": "boolean",
                "description": "Whether a human must classify this finding."
              },
              "evidence": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "evidenceId": {
                      "type": "string",
                      "description": ""
                    },
                    "sourcePath": {
                      "type": "string",
                      "description": ""
                    },
                    "excerpt": {
                      "type": "string",
                      "description": "Sanitized, truncated excerpt."
                    },
                    "lineNumber": {
                      "type": "integer",
                      "description": ""
                    }
                  },
                  "additionalProperties": false
                },
                "description": "Supporting evidence"
              },
              "adrId": {
                "type": "string",
                "description": ""
              },
              "otherAdrId": {
                "type": "string",
                "description": ""
              },
              "claimA": {
                "type": "string",
                "description": ""
              },
              "claimB": {
                "type": "string",
                "description": ""
              },
              "contradictionType": {
                "type": "string",
                "description": ""
              },
              "needsSemanticReview": {
                "type": "boolean",
                "description": "True when the deterministic score is below 0.85 and no semantic analyzer ran; review the two claims and classify with finding_review."
              }
            },
            "additionalProperties": false
          },
          "description": "Contradiction findings"
        },
        "adrsCompared": {
          "type": "integer",
          "description": ""
        },
        "candidatesEvaluated": {
          "type": "integer",
          "description": ""
        },
        "semanticAnalyzerUsed": {
          "type": "boolean",
          "description": ""
        },
        "guidance": {
          "type": "string",
          "description": "How to proceed with needsSemanticReview findings."
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "adrId": "adr-0009",
  "includeDrafts": true
}
```

**Example response (`data`)**

```json
{
  "findings": [
    {
      "id": "ctr_\u2026",
      "kind": "Contradiction",
      "severity": "High",
      "confidence": {
        "score": 0.95,
        "basis": "Observed",
        "rationale": "Both claims declared by humans; deterministic rule match."
      },
      "summary": "ProhibitedVsRequired: adr-0009 vs adr-0003",
      "explanation": "adr-0003 prohibits \u0027Newtonsoft.Json\u0027 (global) while adr-0009 requires \u0027Newtonsoft.Json\u0027 (global).",
      "reviewStatus": "Open",
      "requiresHumanReview": true,
      "adrId": "adr-0003",
      "otherAdrId": "adr-0009",
      "claimA": "must not use Newtonsoft.Json",
      "claimB": "must use Newtonsoft.Json",
      "contradictionType": "ProhibitedVsRequired",
      "needsSemanticReview": false,
      "evidence": [
        {
          "evidenceId": "adr-0003",
          "sourcePath": "adr/adr-0003",
          "excerpt": "We must not use Newtonsoft.Json."
        },
        {
          "evidenceId": "adr-0009",
          "sourcePath": "adr/adr-0009",
          "excerpt": "must use Newtonsoft.Json"
        }
      ]
    }
  ],
  "adrsCompared": 9,
  "candidatesEvaluated": 1,
  "semanticAnalyzerUsed": false,
  "guidance": "All findings are deterministic matches. A human should classify each with finding_review."
}
```

## `adr_safety_review`

**Purpose.** Advisory review of an ADR (stored or inline) for security, availability, data-loss, privacy, compliance, lock-in, cost, EOL technology, scalability, operability, rollback/migration gaps, weak evidence, conflicts, irreversibility, blast radius and responsible-AI concerns. Separates evidence, inference, recommendation and policy; blocks only when organization policy says so.

- Classification: **mutating**, phase **Mvp**, implemented
- Required permission: `AnalysisRun`
- Idempotency: Natural
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `ADR_NOT_FOUND`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "inline": {
      "type": "object",
      "description": "Review unsaved ADR content instead of a stored ADR.",
      "additionalProperties": false,
      "properties": {
        "title": {
          "type": "string",
          "description": "Title.",
          "maxLength": 300
        },
        "context": {
          "type": "string",
          "description": "Context / problem statement.",
          "maxLength": 20000
        },
        "decision": {
          "type": "string",
          "description": "The decision.",
          "maxLength": 20000
        },
        "rationale": {
          "type": "string",
          "description": "Why.",
          "maxLength": 20000
        },
        "alternatives": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Considered alternatives.",
          "maxItems": 50
        },
        "consequences": {
          "type": "string",
          "description": "Consequences.",
          "maxLength": 20000
        },
        "status": {
          "type": "string",
          "description": "Lifecycle status.",
          "enum": [
            "Draft",
            "Proposed",
            "UnderReview",
            "Accepted",
            "Rejected",
            "Deprecated",
            "Superseded",
            "Archived"
          ]
        },
        "owners": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Owners.",
          "maxItems": 50
        },
        "approvers": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Approvers.",
          "maxItems": 50
        },
        "approvalResult": {
          "type": "string",
          "description": "Approval outcome text.",
          "maxLength": 2000
        },
        "humanNotes": {
          "type": "string",
          "description": "Free notes.",
          "maxLength": 20000
        },
        "tags": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Tags.",
          "maxItems": 50
        },
        "claims": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "claimId": {
                "type": "string",
                "description": "Stable id for the claim within the ADR.",
                "maxLength": 64
              },
              "kind": {
                "type": "string",
                "description": "Claim kind.",
                "enum": [
                  "ProhibitedTechnology",
                  "RequiredTechnology",
                  "InfrastructureRequirement",
                  "DatabaseOwnership",
                  "IntegrationStyle",
                  "CloudProvider",
                  "AuthStrategy",
                  "DataResidency",
                  "DataRetention",
                  "Other"
                ]
              },
              "subject": {
                "type": "string",
                "description": "What the claim is about, e.g. \u0027Newtonsoft.Json\u0027, \u0027storage accounts\u0027, \u0027OrderService\u0027.",
                "maxLength": 200
              },
              "predicate": {
                "type": "string",
                "description": "Relation, e.g. \u0027must have\u0027, \u0027owns\u0027.",
                "maxLength": 100
              },
              "object": {
                "type": "string",
                "description": "Target value, e.g. \u0027private endpoint\u0027, \u0027OrdersDb\u0027, \u0027async\u0027.",
                "maxLength": 200
              },
              "scope": {
                "type": "object",
                "properties": {
                  "system": {
                    "type": "string",
                    "description": "System the claim applies to; omit for all.",
                    "maxLength": 100
                  },
                  "boundedContext": {
                    "type": "string",
                    "description": "Bounded context; omit for all.",
                    "maxLength": 100
                  },
                  "environment": {
                    "type": "string",
                    "description": "Environment such as production; omit for all.",
                    "maxLength": 50
                  },
                  "effectiveFrom": {
                    "type": "string",
                    "description": "ISO-8601 date-time."
                  },
                  "effectiveTo": {
                    "type": "string",
                    "description": "ISO-8601 date-time."
                  }
                },
                "additionalProperties": false
              },
              "sourceText": {
                "type": "string",
                "description": "Sentence in the decision this claim comes from.",
                "maxLength": 1000
              }
            },
            "additionalProperties": false,
            "required": [
              "claimId",
              "kind",
              "subject"
            ]
          },
          "description": "Structured, testable claims that drive drift and contradiction detection.",
          "maxItems": 200
        },
        "supersedes": {
          "type": "string",
          "description": "ADR id this one supersedes.",
          "maxLength": 128
        },
        "supersededBy": {
          "type": "string",
          "description": "ADR id that supersedes this one.",
          "maxLength": 128
        }
      }
    },
    "persist": {
      "type": "boolean",
      "description": "Persist findings when adrId is given (default true).",
      "default": true
    }
  },
  "additionalProperties": false
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "findings": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "id": {
                "type": "string",
                "description": "Finding id."
              },
              "kind": {
                "type": "string",
                "description": "Contradiction, Drift, Safety or Governance."
              },
              "severity": {
                "type": "string",
                "description": "Info..Critical."
              },
              "confidence": {
                "type": "object",
                "properties": {
                  "score": {
                    "type": "number",
                    "description": "0..1"
                  },
                  "basis": {
                    "type": "string",
                    "description": "Observed, Declared, Inferred or Uncertain."
                  },
                  "rationale": {
                    "type": "string",
                    "description": "Why."
                  }
                },
                "additionalProperties": false
              },
              "summary": {
                "type": "string",
                "description": "One-line summary."
              },
              "explanation": {
                "type": "string",
                "description": "Reasoning with evidence references."
              },
              "reviewStatus": {
                "type": "string",
                "description": "Open, Confirmed, AcceptableException, FalsePositive, Resolved."
              },
              "requiresHumanReview": {
                "type": "boolean",
                "description": "Whether a human must classify this finding."
              },
              "evidence": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "evidenceId": {
                      "type": "string",
                      "description": ""
                    },
                    "sourcePath": {
                      "type": "string",
                      "description": ""
                    },
                    "excerpt": {
                      "type": "string",
                      "description": "Sanitized, truncated excerpt."
                    },
                    "lineNumber": {
                      "type": "integer",
                      "description": ""
                    }
                  },
                  "additionalProperties": false
                },
                "description": "Supporting evidence"
              },
              "category": {
                "type": "string",
                "description": ""
              },
              "affectedQualityAttributes": {
                "type": "array",
                "items": {
                  "type": "string",
                  "maxLength": 500
                },
                "description": "",
                "maxItems": 50
              },
              "suggestedMitigations": {
                "type": "array",
                "items": {
                  "type": "string",
                  "maxLength": 500
                },
                "description": "",
                "maxItems": 50
              },
              "alternativesToConsider": {
                "type": "array",
                "items": {
                  "type": "string",
                  "maxLength": 500
                },
                "description": "",
                "maxItems": 50
              },
              "policyBlocksAcceptance": {
                "type": "boolean",
                "description": ""
              },
              "policyId": {
                "type": "string",
                "description": ""
              },
              "observedEvidence": {
                "type": "string",
                "description": "What was found in the text."
              },
              "inference": {
                "type": "string",
                "description": "What the rule inferred."
              },
              "recommendation": {
                "type": "string",
                "description": "What to do."
              }
            },
            "additionalProperties": false
          },
          "description": "Findings ordered by severity"
        },
        "humanReviewRequired": {
          "type": "boolean",
          "description": ""
        },
        "policyBlocksAcceptance": {
          "type": "boolean",
          "description": ""
        },
        "rulesEvaluated": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "",
          "maxItems": 50
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "adrId": "adr-0011",
  "persist": true
}
```

**Example response (`data`)**

```json
{
  "findings": [
    {
      "id": "saf_\u2026",
      "kind": "Safety",
      "severity": "High",
      "confidence": {
        "score": 0.55,
        "basis": "Inferred",
        "rationale": "Heuristic text rule; verify against the actual design."
      },
      "summary": "Potential security weakness: Basic authentication",
      "category": "Security",
      "affectedQualityAttributes": [
        "Security",
        "Compliance"
      ],
      "observedEvidence": "Text mentions \u0027basic authentication\u0027.",
      "inference": "Basic authentication weakens the security posture if adopted as described.",
      "recommendation": "Use OAuth 2.0/OIDC with Entra ID or managed identities.",
      "suggestedMitigations": [
        "Use OAuth 2.0/OIDC with Entra ID or managed identities.",
        "Request a security review before acceptance."
      ],
      "alternativesToConsider": [],
      "requiresHumanReview": true,
      "policyBlocksAcceptance": false,
      "reviewStatus": "Open"
    }
  ],
  "humanReviewRequired": true,
  "policyBlocksAcceptance": false,
  "rulesEvaluated": [
    "SR-EOL-001",
    "SR-OPS-001",
    "SR-OPS-002",
    "SR-EVD-001",
    "SR-SEC-001",
    "SR-AVL-001",
    "SR-PRV-001",
    "SR-ECO-001",
    "SR-SCL-001",
    "SR-RSK-001",
    "SR-RAI-001",
    "SR-CON-001"
  ]
}
```

## `finding_review`

**Purpose.** Human classification of a finding: Confirmed, AcceptableException, FalsePositive or Resolved. Classifications persist across re-scans while the underlying result is unchanged.

- Classification: **mutating**, phase **Mvp**, implemented
- Required permission: `FindingReview`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `NOT_FOUND`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "findingId": {
      "type": "string",
      "description": "",
      "maxLength": 128
    },
    "status": {
      "type": "string",
      "description": "",
      "enum": [
        "Confirmed",
        "AcceptableException",
        "FalsePositive",
        "Resolved"
      ]
    },
    "note": {
      "type": "string",
      "description": "",
      "maxLength": 2000
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "findingId",
    "status"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "finding": {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": "Finding id."
            },
            "kind": {
              "type": "string",
              "description": "Contradiction, Drift, Safety or Governance."
            },
            "severity": {
              "type": "string",
              "description": "Info..Critical."
            },
            "confidence": {
              "type": "object",
              "properties": {
                "score": {
                  "type": "number",
                  "description": "0..1"
                },
                "basis": {
                  "type": "string",
                  "description": "Observed, Declared, Inferred or Uncertain."
                },
                "rationale": {
                  "type": "string",
                  "description": "Why."
                }
              },
              "additionalProperties": false
            },
            "summary": {
              "type": "string",
              "description": "One-line summary."
            },
            "explanation": {
              "type": "string",
              "description": "Reasoning with evidence references."
            },
            "reviewStatus": {
              "type": "string",
              "description": "Open, Confirmed, AcceptableException, FalsePositive, Resolved."
            },
            "requiresHumanReview": {
              "type": "boolean",
              "description": "Whether a human must classify this finding."
            },
            "evidence": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "evidenceId": {
                    "type": "string",
                    "description": ""
                  },
                  "sourcePath": {
                    "type": "string",
                    "description": ""
                  },
                  "excerpt": {
                    "type": "string",
                    "description": "Sanitized, truncated excerpt."
                  },
                  "lineNumber": {
                    "type": "integer",
                    "description": ""
                  }
                },
                "additionalProperties": false
              },
              "description": "Supporting evidence"
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "findingId": "ctr_\u2026",
  "status": "AcceptableException",
  "note": "Legacy module; exception approved until Q3."
}
```

**Example response (`data`)**

```json
{
  "finding": {
    "id": "ctr_\u2026",
    "kind": "Contradiction",
    "severity": "High",
    "summary": "ProhibitedVsRequired: adr-0009 vs adr-0003",
    "reviewStatus": "AcceptableException",
    "requiresHumanReview": false,
    "adrId": "adr-0003"
  }
}
```

## `adr_impact_analysis`

**Purpose.** Estimate the impact of changing or superseding an ADR: dependent ADRs, linked evidence, affected services.

- Classification: **read-only**, phase **Later**, **specified only (not exposed by the host until implemented)**
- Required permission: `AdrRead`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `NOT_IMPLEMENTED`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "impact": {
          "description": ""
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `architecture_fitness_check`

**Purpose.** Run executable fitness functions derived from accepted ADR constraints and return pass/fail/unknown with evidence.

- Classification: **read-only**, phase **Later**, **specified only (not exposed by the host until implemented)**
- Required permission: `AnalysisRun`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `NOT_IMPLEMENTED`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "ruleIds": {
      "type": "array",
      "items": {
        "type": "string",
        "maxLength": 500
      },
      "description": "Rules to run; all when omitted.",
      "maxItems": 50
    },
    "scope": {
      "type": "string",
      "description": "",
      "maxLength": 200
    }
  },
  "additionalProperties": false
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "results": {
          "description": ""
        },
        "blocking": {
          "type": "boolean",
          "description": ""
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `dependency_map_generate`

**Purpose.** Generate a dependency map from evidence manifests (asynchronous).

- Classification: **mutating**, phase **Later**, **specified only (not exposed by the host until implemented)**
- Required permission: `AnalysisRun`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `NOT_IMPLEMENTED`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "job": {
          "type": "object",
          "properties": {
            "jobId": {
              "type": "string",
              "description": "Job id."
            },
            "type": {
              "type": "string",
              "description": "Job type."
            },
            "state": {
              "type": "string",
              "description": "Queued, Running, Succeeded, Failed, Cancelled, Expired, Poisoned."
            },
            "progressPercent": {
              "type": "integer",
              "description": "0..100"
            },
            "statusMessage": {
              "type": "string",
              "description": ""
            },
            "createdAt": {
              "type": "string",
              "description": ""
            },
            "completedAt": {
              "type": "string",
              "description": ""
            },
            "errorCode": {
              "type": "string",
              "description": ""
            },
            "expiresAt": {
              "type": "string",
              "description": "When the result is purged."
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `c4_model_generate`

**Purpose.** Generate C4 (context/container) diagrams in Mermaid from ADRs and evidence (asynchronous).

- Classification: **mutating**, phase **Later**, **specified only (not exposed by the host until implemented)**
- Required permission: `AnalysisRun`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `NOT_IMPLEMENTED`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "level": {
      "type": "string",
      "description": "",
      "enum": [
        "context",
        "container"
      ]
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "job": {
          "type": "object",
          "properties": {
            "jobId": {
              "type": "string",
              "description": "Job id."
            },
            "type": {
              "type": "string",
              "description": "Job type."
            },
            "state": {
              "type": "string",
              "description": "Queued, Running, Succeeded, Failed, Cancelled, Expired, Poisoned."
            },
            "progressPercent": {
              "type": "integer",
              "description": "0..100"
            },
            "statusMessage": {
              "type": "string",
              "description": ""
            },
            "createdAt": {
              "type": "string",
              "description": ""
            },
            "completedAt": {
              "type": "string",
              "description": ""
            },
            "errorCode": {
              "type": "string",
              "description": ""
            },
            "expiresAt": {
              "type": "string",
              "description": "When the result is purged."
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `decision_memory_search`

**Purpose.** Search durable, tenant-isolated decision memory (prior tool calls, ADR references, findings, human feedback) with explainable keyword scoring.

- Classification: **read-only**, phase **Extended**, implemented
- Required permission: `MemoryRead`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "query": {
      "type": "string",
      "description": "",
      "maxLength": 500
    },
    "take": {
      "type": "integer",
      "description": "",
      "minimum": 1,
      "maximum": 50,
      "default": 10
    }
  },
  "additionalProperties": false,
  "required": [
    "query"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "hits": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "score": {
                "type": "number",
                "description": ""
              },
              "explanation": {
                "type": "string",
                "description": ""
              },
              "matchedTerms": {
                "type": "array",
                "items": {
                  "type": "string",
                  "maxLength": 500
                },
                "description": "",
                "maxItems": 50
              },
              "timestamp": {
                "type": "string",
                "description": ""
              },
              "tool": {
                "type": "string",
                "description": ""
              },
              "adrIds": {
                "type": "array",
                "items": {
                  "type": "string",
                  "maxLength": 500
                },
                "description": "",
                "maxItems": 50
              },
              "findingIds": {
                "type": "array",
                "items": {
                  "type": "string",
                  "maxLength": 500
                },
                "description": "",
                "maxItems": 50
              },
              "summary": {
                "type": "string",
                "description": ""
              }
            },
            "additionalProperties": false
          },
          "description": ""
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `decision_lineage_get`

**Purpose.** Return the supersession chain and related decisions for an ADR.

- Classification: **read-only**, phase **Later**, **specified only (not exposed by the host until implemented)**
- Required permission: `AdrRead`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `NOT_IMPLEMENTED`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "lineage": {
          "description": ""
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `architecture_rule_create`

**Purpose.** Create an executable architecture rule (fitness function) from an accepted ADR claim (Admin).

- Classification: **mutating**, phase **Later**, **specified only (not exposed by the host until implemented)**
- Required permission: `RuleManage`
- Idempotency: Key
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `NOT_IMPLEMENTED`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "adrId": {
      "type": "string",
      "description": "ADR identifier, e.g. adr-0007.",
      "maxLength": 128,
      "pattern": "^[A-Za-z0-9._:-]\u002B$"
    },
    "rule": {
      "description": "Rule definition."
    },
    "idempotencyKey": {
      "type": "string",
      "description": "Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "adrId",
    "rule"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "rule": {
          "description": ""
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `architecture_rule_validate`

**Purpose.** Validate evidence against one rule.

- Classification: **read-only**, phase **Later**, **specified only (not exposed by the host until implemented)**
- Required permission: `AnalysisRun`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `NOT_IMPLEMENTED`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "ruleId": {
      "type": "string",
      "description": "",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "ruleId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "result": {
          "description": ""
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `governance_check`

**Purpose.** Aggregate governance posture for a workspace: open contradictions, drift, blocked safety findings, stale ADRs.

- Classification: **read-only**, phase **Later**, **specified only (not exposed by the host until implemented)**
- Required permission: `AdrRead`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `NOT_IMPLEMENTED`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    }
  },
  "additionalProperties": false
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "summary": {
          "description": ""
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `deployment_readiness_check`

**Purpose.** Evaluate deployment readiness against blocking fitness functions and open critical findings.

- Classification: **read-only**, phase **Later**, **specified only (not exposed by the host until implemented)**
- Required permission: `AnalysisRun`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `NOT_IMPLEMENTED`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "serviceId": {
      "type": "string",
      "description": "",
      "maxLength": 128
    }
  },
  "additionalProperties": false
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "ready": {
          "type": "boolean",
          "description": ""
        },
        "blockers": {
          "description": ""
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `analysis_job_get`

**Purpose.** Get the state and progress of an asynchronous analysis job.

- Classification: **read-only**, phase **Mvp**, implemented
- Required permission: `JobRead`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `JOB_NOT_FOUND`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "jobId": {
      "type": "string",
      "description": "",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "jobId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "job": {
          "type": "object",
          "properties": {
            "jobId": {
              "type": "string",
              "description": "Job id."
            },
            "type": {
              "type": "string",
              "description": "Job type."
            },
            "state": {
              "type": "string",
              "description": "Queued, Running, Succeeded, Failed, Cancelled, Expired, Poisoned."
            },
            "progressPercent": {
              "type": "integer",
              "description": "0..100"
            },
            "statusMessage": {
              "type": "string",
              "description": ""
            },
            "createdAt": {
              "type": "string",
              "description": ""
            },
            "completedAt": {
              "type": "string",
              "description": ""
            },
            "errorCode": {
              "type": "string",
              "description": ""
            },
            "expiresAt": {
              "type": "string",
              "description": "When the result is purged."
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

**Example request (arguments)**

```json
{
  "jobId": "job_1a\u2026"
}
```

**Example response (`data`)**

```json
{
  "job": {
    "jobId": "job_1a\u2026",
    "type": "EvidenceProcessing",
    "state": "Succeeded",
    "progressPercent": 100,
    "statusMessage": "Completed",
    "createdAt": "\u2026",
    "startedAt": "\u2026",
    "completedAt": "\u2026",
    "expiresAt": "\u2026",
    "attempt": 1
  }
}
```

## `analysis_job_cancel`

**Purpose.** Request cancellation of a queued or running job.

- Classification: **mutating**, phase **Extended**, implemented
- Required permission: `JobCancel`
- Idempotency: Natural
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `JOB_NOT_FOUND`, `JOB_NOT_CANCELLABLE`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "jobId": {
      "type": "string",
      "description": "",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "jobId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "job": {
          "type": "object",
          "properties": {
            "jobId": {
              "type": "string",
              "description": "Job id."
            },
            "type": {
              "type": "string",
              "description": "Job type."
            },
            "state": {
              "type": "string",
              "description": "Queued, Running, Succeeded, Failed, Cancelled, Expired, Poisoned."
            },
            "progressPercent": {
              "type": "integer",
              "description": "0..100"
            },
            "statusMessage": {
              "type": "string",
              "description": ""
            },
            "createdAt": {
              "type": "string",
              "description": ""
            },
            "completedAt": {
              "type": "string",
              "description": ""
            },
            "errorCode": {
              "type": "string",
              "description": ""
            },
            "expiresAt": {
              "type": "string",
              "description": "When the result is purged."
            }
          },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

## `analysis_job_results`

**Purpose.** Fetch the compact result of a completed job.

- Classification: **read-only**, phase **Extended**, implemented
- Required permission: `JobRead`
- Idempotency: ReadOnly
- Failure codes: `VALIDATION_FAILED`, `UNAUTHORIZED`, `FORBIDDEN`, `TENANT_MISMATCH`, `RATE_LIMITED`, `INTERNAL_ERROR`, `JOB_NOT_FOUND`

<details><summary>Input schema</summary>

```json
{
  "type": "object",
  "properties": {
    "workspaceId": {
      "type": "string",
      "description": "Workspace to operate in. Optional; defaults to the caller\u0027s default workspace. Must be a workspace the caller is authorized for.",
      "maxLength": 64,
      "pattern": "^[A-Za-z0-9._-]\u002B$"
    },
    "jobId": {
      "type": "string",
      "description": "",
      "maxLength": 128
    }
  },
  "additionalProperties": false,
  "required": [
    "jobId"
  ]
}
```
</details>

<details><summary>Output schema</summary>

```json
{
  "type": "object",
  "properties": {
    "ok": {
      "type": "boolean",
      "description": "True when the call succeeded."
    },
    "data": {
      "type": "object",
      "properties": {
        "job": {
          "type": "object",
          "properties": {
            "jobId": {
              "type": "string",
              "description": "Job id."
            },
            "type": {
              "type": "string",
              "description": "Job type."
            },
            "state": {
              "type": "string",
              "description": "Queued, Running, Succeeded, Failed, Cancelled, Expired, Poisoned."
            },
            "progressPercent": {
              "type": "integer",
              "description": "0..100"
            },
            "statusMessage": {
              "type": "string",
              "description": ""
            },
            "createdAt": {
              "type": "string",
              "description": ""
            },
            "completedAt": {
              "type": "string",
              "description": ""
            },
            "errorCode": {
              "type": "string",
              "description": ""
            },
            "expiresAt": {
              "type": "string",
              "description": "When the result is purged."
            }
          },
          "additionalProperties": false
        },
        "result": {
          "description": "Job-type specific result."
        }
      },
      "additionalProperties": false
    },
    "error": {
      "type": "object",
      "properties": {
        "code": {
          "type": "string",
          "description": "Stable error code."
        },
        "message": {
          "type": "string",
          "description": "Human-readable message, redacted."
        },
        "retryable": {
          "type": "boolean",
          "description": "Whether retrying may succeed."
        },
        "correlationId": {
          "type": "string",
          "description": "Correlation id for support."
        },
        "details": {
          "description": "Optional structured details."
        }
      },
      "additionalProperties": false
    },
    "meta": {
      "type": "object",
      "properties": {
        "correlationId": {
          "type": "string",
          "description": "Correlation id."
        },
        "tool": {
          "type": "string",
          "description": "Tool name."
        },
        "durationMs": {
          "type": "integer",
          "description": "Server duration."
        },
        "warnings": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 500
          },
          "description": "Non-fatal warnings.",
          "maxItems": 50
        },
        "idempotentReplay": {
          "type": "boolean",
          "description": "True when a stored result was returned for a repeated idempotency key."
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "required": [
    "ok",
    "meta"
  ]
}
```
</details>

