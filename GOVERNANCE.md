# Governance of the SlimFaas Project

## Objectives

SlimFaas is a lightweight Functions-as-a-Service (FaaS) framework designed for efficient and extensible serverless execution, leveraging open standards and a modular approach.

This document defines the governance model of the SlimFaas project to ensure transparent and collaborative decision-making, aligned with the principles of the **Cloud Native Computing Foundation (CNCF)**.

---

## 1. Governance Model

SlimFaas follows an open governance model based on consensus, with a structure defined in three levels:

- **Contributors**: Anyone submitting code, documentation, or suggestions via issues and pull requests.
- **Maintainers**: A group of individuals with write access to the repository responsible for reviewing and approving contributions.
- **Technical Steering Committee (TSC)**: A set of experienced maintainers responsible for the project's strategic decisions.

---

## 2. Roles and Responsibilities

### 2.1 Contributors
Anyone can contribute to SlimFaas by submitting pull requests (PRs), issues, or participating in discussions. Contributors must adhere to the project's **Code of Conduct**.

Contributions use Conventional Commit PR titles and DCO-signed commits. Fork PRs run the same credential-free checks and release builds as internal PRs.

### 2.2 Maintainers
Maintainers are contributors who have demonstrated sustained commitment and technical expertise in SlimFaas. Their responsibilities include:
- Reviewing and approving PRs,
- Managing issues and technical discussions,
- Ensuring code quality and stability,
- Assisting new contributors.

**Release responsibilities**: Maintainers review and merge the automatically prepared release PR. A maintainer can explicitly request an alpha or beta from `main`. Publication credentials are available only to the corresponding protected jobs.

### 2.3 Technical Steering Committee (TSC)
The **Technical Steering Committee (TSC)** is responsible for strategic decisions, including:
- The vision and roadmap of the project,
- The integration of new features,
- Critical decisions (licenses, architecture, partnerships).

The TSC consists of at least three members and makes decisions by consensus. If no consensus is reached, a majority vote is conducted.

---

## 3. Automated Semantic Versioning

Release Please maintains a single release PR containing the common version and changelog. `fix` increments patch, `feat` increments minor, and `!` or a `BREAKING CHANGE:` footer increments major. Other changes do not independently trigger a stable release. Commit keywords never publish releases.

Merging the reviewed release PR approves the release. All distributions are built from its fixed merge SHA, tested before publication, signed, and staged in a GitHub draft. Publication reuses these artifacts and finalizes the release only after registry verification. Tags and published assets must not be overwritten. Main requires CI, DCO, FOSSA and maintainer approval; release files have designated CODEOWNERS.

See the [release guide](docs/releasing.md) for activation, previews, verification and recovery. Repository and registry settings must be activated as documented before this process publishes releases.

---

## 4. Decision-Making Process

SlimFaas follows a **consensus-based governance model**:
- Discussions occur on **GitHub Issues**, **GitHub Discussions**, and the **community Slack**.
- Operational decisions (PR reviews, issue triage) are made by maintainers.
- Strategic decisions (architecture changes, major new features) are made by the **TSC**.

If a decision remains disputed for a long time, it may be subject to a **formal vote**, where each **TSC** member has one vote.

---

## 5. Security Restrictions on Pull Requests

All PRs, including forks, build and test without publishing credentials. No `pull_request_target` workflow checks out contributor code. Secret-dependent SonarCloud analysis runs on trusted internal PRs; the aggregate CI check accounts for this exception. Publication accepts only reviewed commits on `main` after successful checks, with each registry's credentials confined to its publication environment.

---

## 6. Membership and Removal

### 6.1 Adding a Maintainer
A contributor may become a maintainer if they meet the following criteria:
- Has actively contributed for several months (code, documentation, discussions),
- Has demonstrated a strong understanding of the project's architecture and principles,
- Is endorsed by at least two current maintainers.

A new maintainer is approved through a **TSC vote**.

### 6.2 Removing a Maintainer
A maintainer may be removed if:
- They have been inactive for more than six months without justification,
- They violate the Code of Conduct,
- They act against the project's interests.

The removal is validated through a **TSC vote** after discussion.

---

## 7. Code of Conduct

SlimFaas follows the **Contributor Covenant Code of Conduct**, ensuring an open and respectful environment for all contributors. Any violation can be reported to maintainers or the TSC.

---

## 8. Licensing and Intellectual Property

SlimFaas is released under the **Apache 2.0 License**, ensuring its openness and accessibility. All contributions must comply with this license.

---

## 9. Communication

Discussions about SlimFaas take place via:
- **GitHub Issues & Discussions**: For bug tracking and feature improvements.
- **Slack** (or another community platform): For real-time discussions.
- **TSC Meetings** (if necessary): For strategic decision-making.

TSC meetings are publicly announced and accessible to all contributors.

---

## 10. Governance Evolution

This document may be updated as needed. Any modification must be approved by the **TSC** and communicated to the community.

---

