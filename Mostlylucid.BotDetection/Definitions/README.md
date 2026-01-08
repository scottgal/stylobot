# Policy Definitions

This directory contains YAML definitions for built-in action policies, detection policies, and detectors.

## Structure

```
Definitions/
├── Actions/                       # Action policy definitions
│   ├── block.policies.yaml        # Block action policies
│   ├── throttle.policies.yaml     # Throttle action policies
│   ├── logonly.policies.yaml      # LogOnly/shadow policies
│   ├── redirect.policies.yaml     # Redirect policies
│   ├── challenge.policies.yaml    # Challenge policies
│   └── action-policy.schema.json  # JSON Schema for validation
├── Detectors/                     # Detector definitions (future)
└── Policies/                      # Detection policy definitions (future)
```

## Naming Conventions

Policy files use consistent naming:
- `{category}.policies.yaml` - Policy definition files

For detector/component manifests:
- `{name}.detector.yaml` - Detection components (bot detection domain)
- `{name}.sensor.yaml` - Low-level signal extraction components
- `{name}.contributor.yaml` - Higher-level analysis components
- `{name}.gatekeeper.yaml` - Flow control components (early exit)
- `{name}.pipeline.yaml` - Pipeline definitions
- `{name}.entity.yaml` - Entity type definitions

## Inheritance

Policies support inheritance via the `extends` property:

```yaml
policies:
  block-hard:
    type: Block
    extends: block
    description: Hard block - extends base block policy
    status_code: 403
```

When a policy extends another:

1. All properties from the parent are inherited
2. Child properties override parent properties
3. Multiple levels of inheritance are supported
4. Circular references are detected and rejected

## Logging

When policies are loaded, the inheritance chain is logged:

```
[Information] Loading action policy 'block-hard' (inherits: block)
[Information] Loading action policy 'strict-block' (inherits: block-hard -> block)
```

When policies are executed:

```
[Information] Executing policy 'strict-block' [Block] (chain: strict-block -> block-hard -> block)
```

## Embedded Resources

The YAML files in this directory are:

1. Embedded as resources in the assembly
2. Loaded at startup to create built-in policies
3. Can be overridden by user configuration

Users can extend or override any built-in policy in their appsettings.json:

```json
{
  "BotDetection": {
    "ActionPolicies": {
      "my-custom-block": {
        "Type": "Block",
        "Extends": "block-hard",
        "Description": "My custom block with different message",
        "Message": "Custom access denied message"
      }
    }
  }
}
```

## Backward Compatibility

Both JSON and YAML formats are supported:
- YAML files with `.policies.yaml` suffix are loaded first (preferred)
- JSON files with `-policies.json` suffix are loaded for backward compatibility
- Properties can use either `snake_case` (YAML) or `PascalCase` (JSON)

## Schema Validation

Each JSON file can reference the schema for editor support:

```json
{
  "$schema": "./action-policy.schema.json",
  ...
}
```

For YAML files, you can use VS Code YAML extension with schema association.
