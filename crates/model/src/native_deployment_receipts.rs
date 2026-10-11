//! Immutable settlement evidence for complete owning deployment activation.
//! A receipt is inserted only inside finish_push's original activation
//! transaction. Its presence confirms a historical commit; absence cannot
//! authorize replay because an in-flight activation may still commit.
use std::{
    collections::BTreeMap,
    sync::LazyLock,
};

use common::{
    document::{
        ParseDocument,
        ParsedDocument,
        CREATION_TIME_FIELD_PATH,
    },
    obj,
    query::{
        IndexRange,
        IndexRangeExpression,
        Order,
        Query,
    },
    runtime::Runtime,
    types::WriteTimestamp,
};
use database::{
    unauthorized_error,
    ResolvedQuery,
    SystemMetadataModel,
    Transaction,
};
use errors::ErrorMetadata;
use serde::{
    Deserialize,
    Serialize,
};
use sync_types::Timestamp;
use value::{
    ConvexObject,
    ConvexValue,
    FieldPath,
    TableName,
    TableNamespace,
};

use crate::{
    SystemIndex,
    SystemTable,
};

pub const NATIVE_DEPLOYMENT_RECEIPTS_TABLE: TableName =
    TableName::const_new("_native_deployment_receipts");
static OPERATION_ID_FIELD: LazyLock<FieldPath> =
    LazyLock::new(|| "operationId".parse().expect("invalid built-in field"));
pub static NATIVE_DEPLOYMENT_RECEIPTS_BY_ID: LazyLock<SystemIndex<NativeDeploymentReceiptsTable>> =
    LazyLock::new(|| {
        SystemIndex::new(
            "by_operation_id",
            [&OPERATION_ID_FIELD, &CREATION_TIME_FIELD_PATH],
        )
        .unwrap()
    });

#[derive(Clone, Debug, Deserialize, Serialize, Eq, PartialEq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NativeDeploymentReceipt {
    pub operation_id: String,
    pub start_push_sha256: String,
}

pub fn validate_operation_id(id: &str) -> anyhow::Result<()> {
    anyhow::ensure!(
        id.len() == 32
            && id
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)),
        ErrorMetadata::bad_request(
            "InvalidNativeDeploymentReceipt",
            "Invalid deployment operation ID"
        )
    );
    Ok(())
}

impl NativeDeploymentReceipt {
    pub fn validate(&self) -> anyhow::Result<()> {
        validate_operation_id(&self.operation_id)?;
        anyhow::ensure!(
            self.start_push_sha256.len() == 64
                && self
                    .start_push_sha256
                    .bytes()
                    .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)),
            ErrorMetadata::bad_request(
                "InvalidNativeDeploymentReceipt",
                "Invalid deployment snapshot digest"
            )
        );
        Ok(())
    }
}

impl TryFrom<NativeDeploymentReceipt> for ConvexObject {
    type Error = anyhow::Error;

    fn try_from(receipt: NativeDeploymentReceipt) -> anyhow::Result<Self> {
        receipt.validate()?;
        obj!("operationId" => receipt.operation_id, "startPushSha256" => receipt.start_push_sha256)
    }
}
impl TryFrom<ConvexObject> for NativeDeploymentReceipt {
    type Error = anyhow::Error;

    fn try_from(object: ConvexObject) -> anyhow::Result<Self> {
        let mut fields: BTreeMap<_, _> = object.into();
        // ResolvedDocument parsing includes owning document metadata. These
        // two fields are added by SystemMetadataModel, not receipt authors.
        fields.remove("_id");
        fields.remove("_creationTime");
        let Some(ConvexValue::String(id)) = fields.remove("operationId") else {
            anyhow::bail!("invalid deployment receipt operationId");
        };
        let Some(ConvexValue::String(digest)) = fields.remove("startPushSha256") else {
            anyhow::bail!("invalid deployment receipt startPushSha256");
        };
        anyhow::ensure!(fields.is_empty(), "unknown deployment receipt field");
        let receipt = Self {
            operation_id: id.to_string(),
            start_push_sha256: digest.to_string(),
        };
        receipt.validate()?;
        Ok(receipt)
    }
}

pub struct NativeDeploymentReceiptsTable;
impl SystemTable for NativeDeploymentReceiptsTable {
    type Metadata = NativeDeploymentReceipt;

    const TABLE_NAME: TableName = NATIVE_DEPLOYMENT_RECEIPTS_TABLE;

    fn indexes() -> Vec<SystemIndex<Self>> {
        vec![NATIVE_DEPLOYMENT_RECEIPTS_BY_ID.clone()]
    }
}

pub struct NativeDeploymentReceiptModel<'a, RT: Runtime> {
    tx: &'a mut Transaction<RT>,
}
impl<'a, RT: Runtime> NativeDeploymentReceiptModel<'a, RT> {
    pub fn new(tx: &'a mut Transaction<RT>) -> Self {
        Self { tx }
    }

    pub async fn get(
        &mut self,
        operation_id: &str,
    ) -> anyhow::Result<Option<(NativeDeploymentReceipt, Timestamp)>> {
        if !(self.tx.identity().is_admin() || self.tx.identity().is_system()) {
            anyhow::bail!(unauthorized_error("get_native_deployment_receipt"));
        }
        validate_operation_id(operation_id)?;
        let query = Query::index_range(IndexRange {
            index_name: NATIVE_DEPLOYMENT_RECEIPTS_BY_ID.name(),
            range: vec![IndexRangeExpression::Eq(
                OPERATION_ID_FIELD.clone(),
                ConvexValue::try_from(operation_id)?.into(),
            )],
            order: Order::Asc,
        });
        let mut stream = ResolvedQuery::new(self.tx, TableNamespace::Global, query)?;
        let Some((document, timestamp)) = stream.next_with_ts(self.tx, None).await? else {
            return Ok(None);
        };
        anyhow::ensure!(
            stream.next(self.tx, Some(1)).await?.is_none(),
            "duplicate deployment receipts"
        );
        let WriteTimestamp::Committed(timestamp) = timestamp else {
            anyhow::bail!("uncommitted deployment receipt read");
        };
        let parsed: ParsedDocument<NativeDeploymentReceipt> = document.parse()?;
        Ok(Some((parsed.into_value(), timestamp)))
    }

    /// The indexed read joins the same OCC read set as activation, preventing
    /// concurrent deliveries of one operation ID from both committing.
    pub async fn record(&mut self, receipt: &NativeDeploymentReceipt) -> anyhow::Result<()> {
        receipt.validate()?;
        if let Some((previous, _)) = self.get(&receipt.operation_id).await? {
            let code = if previous == *receipt {
                "DeploymentAlreadyCommitted"
            } else {
                "NativeDeploymentReceiptConflict"
            };
            anyhow::bail!(ErrorMetadata::bad_request(
                code,
                "Deployment operation ID has already committed; read its owning receipt"
            ));
        }
        SystemMetadataModel::new_global(self.tx)
            .insert_metadata(
                &NATIVE_DEPLOYMENT_RECEIPTS_TABLE,
                receipt.clone().try_into()?,
            )
            .await?;
        Ok(())
    }
}
