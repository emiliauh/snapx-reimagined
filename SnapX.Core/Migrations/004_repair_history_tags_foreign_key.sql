-- Migration: 004_repair_history_tags_foreign_key.sql
-- Created: 2026-08-25
-- Description: Repairs the stale Tags foreign key left by migration 003.
--
-- SQLite rewrites inbound foreign keys when HistoryItems is renamed to
-- _old_HistoryItems. Migration 003 rebuilds the table, but the pre-existing
-- Tags table consequently keeps pointing at the dropped temporary name. Any
-- later history append with tags then fails with "no such table:
-- main._old_HistoryItems". Rebuild Tags so existing tag data is retained and
-- new rows reference the replacement HistoryItems table.

ALTER TABLE Tags RENAME TO _old_Tags;

CREATE TABLE Tags (
  Id INTEGER NOT NULL CONSTRAINT PK_Tags PRIMARY KEY AUTOINCREMENT,
  Text TEXT NOT NULL,
  WindowTitle TEXT NULL,
  ProcessName TEXT NULL,
  HistoryItemId INTEGER NULL,
  CONSTRAINT FK_Tags_HistoryItems_HistoryItemId
    FOREIGN KEY (HistoryItemId) REFERENCES HistoryItems (Id) ON DELETE CASCADE
);

INSERT INTO Tags (Id, Text, WindowTitle, ProcessName, HistoryItemId)
SELECT Id, Text, WindowTitle, ProcessName, HistoryItemId
FROM _old_Tags;

DROP TABLE _old_Tags;
