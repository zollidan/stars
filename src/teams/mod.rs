//! Справочник команд: router -> service -> repository.

mod identities;
#[cfg(test)]
mod integration_tests;
mod matching;
mod repository;
mod router;
mod schemas;
mod service;
mod worker;

pub use router::router;
pub use worker::spawn as spawn_matcher;
