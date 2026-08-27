"use strict";

const fs = require("node:fs");

const changeSummaryStart = "<!-- change-summary:start -->";
const changeSummaryEnd = "<!-- change-summary:end -->";
const ciResultsStart = "<!-- ci-results:start -->";
const ciResultsEnd = "<!-- ci-results:end -->";

const changeTypes = [
    ["feat", "Features"],
    ["fix", "Fixes"],
    ["refactor", "Refactoring"],
    ["test", "Tests"],
    ["docs", "Documentation"],
    ["ci", "CI"],
    ["chore", "Maintenance"],
    ["other", "Other changes"],
];

function capitalize(value) {
    if (!value) {
        return value;
    }

    return value.charAt(0).toUpperCase() + value.slice(1);
}

function parseCommitSubject(subject) {
    const conventionalCommitPattern =
        /^(feat|fix|refactor|test|docs|ci|chore)(?:\(([^)]+)\))?!?:\s+(.+)$/i;

    const match = subject.match(conventionalCommitPattern);

    if (!match) {
        return {
            type: "other",
            description: capitalize(subject),
        };
    }

    const [, type, scope, description] = match;

    return {
        type: type.toLowerCase(),
        scope,
        description: capitalize(description),
    };
}

function createChangeSummary(commits, pullRequest) {
    const groups = new Map(
        changeTypes.map(([type]) => [type, []]));

    let includedCommitCount = 0;

    for (const commit of commits) {
        const subject = commit.commit.message
            .split(/\r?\n/, 1)[0]
            .trim();

        if (!subject || subject.startsWith("Merge ")) {
            continue;
        }

        const parsedCommit = parseCommitSubject(subject);

        const bullet = parsedCommit.scope
            ? `- **${parsedCommit.scope}:** ${parsedCommit.description}`
            : `- ${parsedCommit.description}`;

        groups.get(parsedCommit.type).push(bullet);
        includedCommitCount++;
    }

    const sections = [];

    for (const [type, title] of changeTypes) {
        const entries = groups.get(type);

        if (entries.length === 0) {
            continue;
        }

        sections.push(
            `### ${title}\n\n${entries.join("\n")}`);
    }

    if (sections.length === 0) {
        sections.push("No commit summary is available.");
    }

    const statistics = [
        "| Metric | Value |",
        "| --- | ---: |",
        `| Commits | ${includedCommitCount} |`,
        `| Files changed | ${pullRequest.changed_files} |`,
        `| Additions | +${pullRequest.additions} |`,
        `| Deletions | -${pullRequest.deletions} |`,
    ].join("\n");

    return `${sections.join("\n\n")}\n\n${statistics}`;
}

function replaceGeneratedSection(
    body,
    startMarker,
    endMarker,
    content) {
    const startIndex = body.indexOf(startMarker);

    if (startIndex < 0) {
        return null;
    }

    const endIndex = body.indexOf(
        endMarker,
        startIndex + startMarker.length);

    if (endIndex < 0) {
        return null;
    }

    const before = body.slice(0, startIndex);
    const after = body.slice(endIndex + endMarker.length);

    return (
        `${before}${startMarker}\n\n` +
        `${content.trim()}\n\n` +
        `${endMarker}${after}`
    );
}

module.exports = async function updatePullRequestDescription({
    github,
    context,
    core,
}) {
    const owner = context.repo.owner;
    const repo = context.repo.repo;
    const pullNumber = context.payload.pull_request.number;

    const { data: pullRequest } = await github.rest.pulls.get({
        owner,
        repo,
        pull_number: pullNumber,
    });

    const commits = await github.paginate(
        github.rest.pulls.listCommits,
        {
            owner,
            repo,
            pull_number: pullNumber,
            per_page: 100,
        });

    const changeSummary = createChangeSummary(
        commits,
        pullRequest);

    const ciReportPath = process.env.CI_REPORT_PATH;

    const ciReport =
        ciReportPath && fs.existsSync(ciReportPath)
            ? fs.readFileSync(ciReportPath, "utf8").trim()
            : "CI report could not be generated. Check the workflow run.";

    const originalBody = pullRequest.body ?? "";
    let updatedBody = originalBody;
    let updatedSectionCount = 0;

    const bodyWithChangeSummary = replaceGeneratedSection(
        updatedBody,
        changeSummaryStart,
        changeSummaryEnd,
        changeSummary);

    if (bodyWithChangeSummary === null) {
        core.warning(
            "The pull request description does not contain change-summary markers.");
    }
    else {
        updatedBody = bodyWithChangeSummary;
        updatedSectionCount++;
    }

    const bodyWithCiResults = replaceGeneratedSection(
        updatedBody,
        ciResultsStart,
        ciResultsEnd,
        ciReport);

    if (bodyWithCiResults === null) {
        core.warning(
            "The pull request description does not contain ci-results markers.");
    }
    else {
        updatedBody = bodyWithCiResults;
        updatedSectionCount++;
    }

    if (updatedSectionCount === 0) {
        core.warning(
            "The pull request description was not updated because no markers were found.");

        return;
    }

    if (updatedBody === originalBody) {
        core.info("The pull request description is already up to date.");

        return;
    }

    await github.rest.pulls.update({
        owner,
        repo,
        pull_number: pullNumber,
        body: updatedBody,
    });

    core.info(
        `Updated ${updatedSectionCount} generated pull request section(s).`);
};
