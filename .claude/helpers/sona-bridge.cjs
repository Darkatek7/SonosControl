#!/usr/bin/env node
// SONA_PATCH_v1 — Bridge between CJS hook-handler and ESM learning-service
// Calls learning-service.mjs as subprocess to avoid CJS/ESM issues
'use strict';

const { execFileSync } = require('child_process');
const path = require('path');
const fs = require('fs');

const helpersDir = __dirname;
const projectRoot = path.resolve(helpersDir, '../..');
const learningService = path.join(helpersDir, 'learning-service.mjs');
const learningHooks = path.join(helpersDir, 'learning-hooks.sh');

// Check if better-sqlite3 is available (required by learning-service.mjs)
function hasBetterSqlite3() {
  try {
    // Check project node_modules
    const localPath = path.join(projectRoot, 'node_modules', 'better-sqlite3');
    if (fs.existsSync(localPath)) return true;
    // Check shared ruflo cache
    const sharedPath = path.join(require('os').homedir(), '.octoally', 'ruflo', 'node_modules', 'better-sqlite3');
    if (fs.existsSync(sharedPath)) return true;
    return false;
  } catch { return false; }
}

function callLearningService(command, args) {
  if (!fs.existsSync(learningService)) return null;
  if (!hasBetterSqlite3()) return null;
  try {
    const result = execFileSync('node', [learningService, command, ...args], {
      cwd: projectRoot,
      timeout: 10000,
      encoding: 'utf-8',
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    return result.trim();
  } catch (e) {
    return null;
  }
}

function callLearningHooks(command, args) {
  if (!fs.existsSync(learningHooks)) return null;
  try {
    const result = execFileSync('bash', [learningHooks, command, ...args], {
      cwd: projectRoot,
      timeout: 10000,
      encoding: 'utf-8',
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    return result.trim();
  } catch (e) {
    return null;
  }
}

// Pending insights log — same file intelligence.cjs writes to on each edit
const PENDING_PATH = path.join(projectRoot, '.claude-flow', 'data', 'pending-insights.jsonl');
const EDIT_PROMOTION_THRESHOLD = 3; // same as ruflo's intelligence.cjs consolidate()

function consolidateEdits() {
  if (!fs.existsSync(PENDING_PATH)) return 0;
  try {
    const lines = fs.readFileSync(PENDING_PATH, 'utf-8').trim().split('\n').filter(Boolean);
    if (lines.length === 0) return 0;
    const editCounts = {};
    for (const line of lines) {
      try {
        const insight = JSON.parse(line);
        if (insight.file) editCounts[insight.file] = (editCounts[insight.file] || 0) + 1;
      } catch {}
    }
    let stored = 0;
    for (const [file, count] of Object.entries(editCounts)) {
      if (count >= EDIT_PROMOTION_THRESHOLD) {
        const shortFile = file.split('/').slice(-2).join('/');
        callLearningService('store', ['Hot path: ' + shortFile + ' edited ' + count + 'x this session', 'edit-pattern']);
        stored++;
      }
    }
    return stored;
  } catch { return 0; }
}

module.exports = {
  sessionStart(sessionId) {
    const result = callLearningHooks('session-start', sessionId ? [sessionId] : []);
    if (result) return result;
    return callLearningService('init', sessionId ? [sessionId] : []);
  },

  sessionEnd() {
    const promoted = consolidateEdits();
    const result = callLearningHooks('session-end', []);
    if (result) return (promoted > 0 ? '[SONA] Promoted ' + promoted + ' edit patterns. ' : '') + result;
    const consolidateResult = callLearningService('consolidate', []);
    return (promoted > 0 ? '[SONA] Promoted ' + promoted + ' edit patterns. ' : '') + (consolidateResult || '');
  },

  storePattern(strategy, domain) {
    return callLearningService('store', [strategy, domain || 'general']);
  },

  searchPatterns(query, k) {
    return callLearningService('search', [query, String(k || 5)]);
  },

  isAvailable() {
    return fs.existsSync(learningService) && hasBetterSqlite3();
  },
};
