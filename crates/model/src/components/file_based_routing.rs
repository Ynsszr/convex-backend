use std::collections::{
    btree_map::Entry,
    BTreeMap,
};

use common::{
    bootstrap_model::components::definition::ComponentExport,
    components::Reference,
};
use errors::ErrorMetadata;
use sync_types::{
    path::PathComponent,
    CanonicalizedModulePath,
    CanonicalizedUdfPath,
    FunctionName,
    ModulePath,
};

use crate::modules::module_versions::{
    AnalyzedModule,
    Visibility,
};

pub fn file_based_exports<'a>(
    functions: impl IntoIterator<Item = (&'a CanonicalizedModulePath, &'a AnalyzedModule)>,
) -> anyhow::Result<BTreeMap<PathComponent, ComponentExport>> {
    let mut exports = BTreeMap::new();
    for (module_path, module) in functions {
        let stripped = module_path.clone().strip();

        let identifiers = stripped
            .components()
            .collect::<Result<Vec<_>, _>>()
            .map_err(|e| ErrorMetadata::bad_request("InvalidModulePath", e.to_string()))?;
        for function in &module.functions {
            if function.visibility != Some(Visibility::Public) {
                continue;
            }
            let mut path = identifiers.clone();
            path.push(function.name.clone().into());
            let (last, prefix) = path.split_last().unwrap();

            let mut current = &mut exports;
            for identifier in prefix {
                let current_node = current
                    .entry(identifier.clone())
                    .or_insert_with(|| ComponentExport::Branch(BTreeMap::new()));
                current = match current_node {
                    ComponentExport::Branch(branch) => branch,
                    ComponentExport::Leaf(..) => anyhow::bail!(ErrorMetadata::bad_request(
                        "InvalidExport",
                        format!(
                            "Path {module_path:?}:{} conflicts with existing export",
                            function.name
                        )
                    )),
                }
            }
            match current.entry(last.clone()) {
                Entry::Vacant(e) => {
                    let path =
                        CanonicalizedUdfPath::new(module_path.clone(), function.name.clone());
                    let reference = Reference::Function(path);
                    e.insert(ComponentExport::Leaf(reference));
                },
                Entry::Occupied(_) => anyhow::bail!(ErrorMetadata::bad_request(
                    "InvalidExport",
                    format!(
                        "Path {module_path:?}:{} conflicts with existing export",
                        function.name
                    )
                )),
            }
        }
    }
    Ok(exports)
}

pub fn export_to_udf_path(attributes: &[PathComponent]) -> anyhow::Result<CanonicalizedUdfPath> {
    let Some((last, prefix)) = attributes.split_last() else {
        anyhow::bail!("Expected at least one path component");
    };
    let mut module_path = String::new();
    let mut first = true;
    for attribute in prefix {
        if !first {
            module_path.push('/');
        }
        module_path.push_str(&attribute[..]);
        first = false;
    }
    let module_path: ModulePath = module_path.parse()?;
    let name: FunctionName = last.parse()?;
    Ok(CanonicalizedUdfPath::new(module_path.canonicalize(), name))
}

/// Select a leaf from the persisted, typechecked component export tree.
/// A child-component branch reexport appends the remaining attributes; a value
/// or function leaf never silently accepts trailing path components.
pub fn select_export_reference(
    exports: &std::collections::BTreeMap<
        sync_types::path::PathComponent,
        common::bootstrap_model::components::definition::ComponentExport,
    >,
    attributes: &[sync_types::path::PathComponent],
) -> Option<common::components::Reference> {
    use common::{
        bootstrap_model::components::definition::ComponentExport,
        components::Reference,
    };
    let mut current = exports;
    for (index, attribute) in attributes.iter().enumerate() {
        match current.get(attribute)? {
            ComponentExport::Branch(next) => current = next,
            ComponentExport::Leaf(reference) => {
                let remaining = &attributes[index + 1..];
                if remaining.is_empty() {
                    return Some(reference.clone());
                }
                if let Reference::ChildComponent {
                    component,
                    attributes,
                } = reference
                {
                    let mut selected = attributes.clone();
                    selected.extend_from_slice(remaining);
                    return Some(Reference::ChildComponent {
                        component: component.clone(),
                        attributes: selected,
                    });
                }
                return None;
            },
        }
    }
    None
}

#[cfg(test)]
mod explicit_export_tests {
    use std::collections::BTreeMap;

    use common::{
        bootstrap_model::components::definition::ComponentExport,
        components::Reference,
    };
    use sync_types::path::PathComponent;

    use super::select_export_reference;

    fn path(parts: &[&str]) -> Vec<PathComponent> {
        parts.iter().map(|part| part.parse().unwrap()).collect()
    }

    #[test]
    fn explicit_alias_resolves_exact_function_without_trailing_attributes() {
        let reference = Reference::Function("counter:read".parse().unwrap());
        let exports = BTreeMap::from([(
            "read".parse().unwrap(),
            ComponentExport::Leaf(reference.clone()),
        )]);
        assert_eq!(
            select_export_reference(&exports, &path(&["read"])),
            Some(reference)
        );
        assert_eq!(
            select_export_reference(&exports, &path(&["read", "extra"])),
            None
        );
        assert_eq!(
            select_export_reference(&exports, &path(&["counter", "read"])),
            None
        );
    }

    #[test]
    fn child_branch_reexport_preserves_remaining_export_path() {
        let reference = Reference::ChildComponent {
            component: "inner".parse().unwrap(),
            attributes: path(&["api"]),
        };
        let exports =
            BTreeMap::from([("nested".parse().unwrap(), ComponentExport::Leaf(reference))]);
        assert_eq!(
            select_export_reference(&exports, &path(&["nested", "counter", "read"])),
            Some(Reference::ChildComponent {
                component: "inner".parse().unwrap(),
                attributes: path(&["api", "counter", "read"])
            })
        );
    }
}
