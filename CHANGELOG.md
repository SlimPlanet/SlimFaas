# Changelog

## v0.84.3

- [96914ddf](https://github.com/SlimPlanet/SlimFaas/commit/96914ddf5bba855a70eda9a14e9a89ba52f9d1df) - fix: resume CPU rate limiting after idle recovery (#345) (release), 2026-09-10 by *Guillaume Chervet*


## 0.84.2



## v0.84.2

- [59a8bb73](https://github.com/SlimPlanet/SlimFaas/commit/59a8bb73bf876199cb9aad8a091f9f6321bbae58) - fix: enforce scale-down policy budgets (#350) (release), 2026-09-10 by *Guillaume Chervet*


## 0.84.1



## v0.84.1

- [a7116414](https://github.com/SlimPlanet/SlimFaas/commit/a711641435dbaac74f3ec6140f2ab956f6c030d7) - perf: event-driven Kubernetes sync via watch streams and jobs N+1 fix (#340) (release), 2026-09-10 by *Guillaume Delahaye*


## 0.84.0



## v0.84.0

- [f61f8c65](https://github.com/SlimPlanet/SlimFaas/commit/f61f8c65f2486b592b685ea2c5e649c58038dc31) - feat: add external autoscaling sources with opt-in wake-up (#341) (release), 2026-09-10 by *Guillaume Chervet*


## 0.83.0



## v0.83.0

- [ef2f8c58](https://github.com/SlimPlanet/SlimFaas/commit/ef2f8c58bb88a95f9c849fea9ef71334af087a72) - feat(SlimFaas): Modernize the dashboard with scalable traffic, data and instance logs (release) (#339), 2026-09-09 by *Guillaume Chervet*


## 0.82.7



## v0.82.7

- [ffff804f](https://github.com/SlimPlanet/SlimFaas/commit/ffff804f82f23bdb1cedca3211be9a63b7424084) - fix: release job concurrency slots before TTL cleanup (#337) (release), 2026-09-08 by *Guillaume Chervet*


## 0.82.6



## v0.82.6

- [b8a688ea](https://github.com/SlimPlanet/SlimFaas/commit/b8a688eadda28cb6e243ddb775fc990f9e398791) - docs: Restore CloMonitor Legal compliance detection (#336) (release), 2026-09-08 by *Copilot*


## 0.82.5



## v0.82.5

- [48b7fc86](https://github.com/SlimPlanet/SlimFaas/commit/48b7fc869de28f2ac6e23d208371bb000f5dc39f) - docs: add guided onboarding, feature diagrams and standalone local demos (#334) (release), 2026-09-08 by *Guillaume Chervet*


## 0.82.4



## v0.82.4

- [8a7fb497](https://github.com/SlimPlanet/SlimFaas/commit/8a7fb49773b8481d5bb6843d44875a510fe38909) - chore: update .NET and client dependencies (release) (#330), 2026-09-02 by *Guillaume Chervet*


## 0.82.3



## v0.82.3

- [aae3e8d8](https://github.com/SlimPlanet/SlimFaas/commit/aae3e8d88599914304aed584063a83257b2930b6) - fix(slimdata): make key-value reads linearizable (#329) (release), 2026-09-02 by *Guillaume Chervet*


## 0.82.2



## v0.82.2

- [19a3e5b7](https://github.com/SlimPlanet/SlimFaas/commit/19a3e5b7b539416fa11ed7991aa966772deef96c) - fix: planet-saver clean npm packages (release), 2026-09-01 by *Guillaume Chervet*


## 0.82.1



## v0.82.1

- [381f9b0c](https://github.com/SlimPlanet/SlimFaas/commit/381f9b0c6a65035d9a507df4a2a17ecb77e53573) - fix: npm pubish token via oidc (release), 2026-09-01 by *Guillaume Chervet*


## 0.82.0



## v0.82.0

- [78c7387d](https://github.com/SlimPlanet/SlimFaas/commit/78c7387d3a16e83dc49fd24baa4e27ff9dcbe802) - fix(slimplanet): Content-Length was set to 0 (#327) (release), 2026-08-28 by *antoinelrnld*
- [35a66252](https://github.com/SlimPlanet/SlimFaas/commit/35a662523fe83be3f184b00d81b14a59e69fe0e7) - feat: Improve SlimData/RAFT recovery under load (#317)(release), 2026-08-21 by *Copilot*
- [e357700e](https://github.com/SlimPlanet/SlimFaas/commit/e357700ec4e1202f27d0cce5a6a696078d377a7a) - doc: Elevate CNCF presence in README header and de-duplicate community content (#315), 2026-08-14 by *Copilot*


## 0.81.1



## v0.81.1

- [5d766c95](https://github.com/SlimPlanet/SlimFaas/commit/5d766c95aafaab9da2c280cea34585f687f65a09) - perf: hot-path reads with zero allocation (~89×), queue counts ~25×, schedule evaluation ~52× faster (release) (#313), 2026-08-14 by *Guillaume Delahaye*


## 0.81.0

- [3cb41f11](https://github.com/SlimPlanet/SlimFaas/commit/3cb41f1177b5217e132b12b286d74820f0fe6e40) - fix: Stabilize Raft cluster catch-up timeout in SlimData test (#312), 2026-08-07 by *Copilot*


## v0.81.0

- [8dc88d7a](https://github.com/SlimPlanet/SlimFaas/commit/8dc88d7a651df1c796d54e523bf47c5b7bce168c) - feat: optimize async (release) (#311), 2026-08-07 by *Guillaume Chervet*


## 0.80.0



## v0.80.0

- [780153dc](https://github.com/SlimPlanet/SlimFaas/commit/780153dc307eee841e3960a73d59dc065fd104e7) - fix: SyncFunction configurations (release), 2026-08-04 by *Guillaume Chervet*
- [309a2119](https://github.com/SlimPlanet/SlimFaas/commit/309a21194601a8334c1ab314552d3bb257477e48) - fix: SyncFunction configurations (release), 2026-08-04 by *Guillaume Chervet*
- [b9f9a9f3](https://github.com/SlimPlanet/SlimFaas/commit/b9f9a9f3e354dc5872b97d124f9b31a3be8b4e15) - feat: optimise sync request (#310), 2026-08-04 by *Guillaume Chervet*


## 0.79.4



## v0.79.4

- [bbe315ad](https://github.com/SlimPlanet/SlimFaas/commit/bbe315ad2270a3c03426490a1e756bfeed9adaa6) - fix: enhance scale (#309) (release), 2026-08-02 by *Guillaume Chervet*


## 0.79.3



## v0.79.3

- [56e8f9a3](https://github.com/SlimPlanet/SlimFaas/commit/56e8f9a309178447723fcc3574e88347c3de1ebc) - refactor(slimfaas): remove trimming warning (#307) (release), 2026-08-01 by *Guillaume Chervet*
- [ec92b95f](https://github.com/SlimPlanet/SlimFaas/commit/ec92b95fe362903392304f95c82d5f501818dafa) - fix(slimfaas): local mode win (#308), 2026-08-01 by *Guillaume Chervet*
- [8fc80e1e](https://github.com/SlimPlanet/SlimFaas/commit/8fc80e1e25d04d8ac6f9e7d92de6dcdade5a7c3b) - doc: Generate `sitemap.xml` dynamically during SlimFaasSite export (#306), 2026-08-01 by *Copilot*


## 0.79.2



## v0.79.2

- [769ffebc](https://github.com/SlimPlanet/SlimFaas/commit/769ffebcf276d0b149e9381675a4a931341b3798) - refactor: clean code (release), 2026-07-31 by *Guillaume Chervet*
- [e209ac86](https://github.com/SlimPlanet/SlimFaas/commit/e209ac86fad58f167446a17a27266fcac81e0f45) - refactor(slimfaas): clean logger warning, 2026-07-31 by *Guillaume Chervet*
- [015280f3](https://github.com/SlimPlanet/SlimFaas/commit/015280f3706b1b279403a03f6703cc4c67e30fdc) - doc: update AGENTS.md, 2026-07-31 by *Guillaume Chervet*


## 0.79.1



## v0.79.1

- [70f8f469](https://github.com/SlimPlanet/SlimFaas/commit/70f8f469b7aea1365fe9a47ef23feb26e4f02249) - fix(slimfaas): local scale down (release), 2026-07-30 by *Guillaume Chervet*


## 0.79.0



## v0.79.0

- [2216e9f6](https://github.com/SlimPlanet/SlimFaas/commit/2216e9f64d0a972f7b5694ca5366d7f8b8ebd0c4) - feat(slimfaas): UI stream job activity (release), 2026-07-30 by *Guillaume Chervet*


## 0.78.0



## v0.78.0

- [53b82316](https://github.com/SlimPlanet/SlimFaas/commit/53b82316abdb890ec06da11eea787ad6a12b9a67) - feat(slimfaas): local add dependson processes (release), 2026-07-30 by *Guillaume Chervet*


## 0.77.1



## v0.77.1

- [661414a9](https://github.com/SlimPlanet/SlimFaas/commit/661414a9073c2a15e6d83a2f5b26ab676c958a13) - feature(slimfaas):  enhance local mode (#305) (release), 2026-07-29 by *Guillaume Chervet*


## 0.76.1



## 0.77.0



## v0.77.0

- [d8de1bee](https://github.com/SlimPlanet/SlimFaas/commit/d8de1beec424685dbb712ca4cdbde7210db30c7d) - feat(slimfaas): local process (release) (#304), 2026-07-28 by *Guillaume Chervet*


## v0.76.1

- [d25deb5c](https://github.com/SlimPlanet/SlimFaas/commit/d25deb5c13b7a477d7b3a6c4e00961d617f63a5e) - feature(slimfaas): add mode local (#303) (release), 2026-07-28 by *Guillaume Chervet*


## 0.76.0



## v0.76.0

- [4ea80568](https://github.com/SlimPlanet/SlimFaas/commit/4ea80568c21d7164adfbb0ccdda58402e60e2430) - feat: multi batch and low latency (#302) (release), 2026-07-26 by *Guillaume Chervet*


## 0.75.0



## v0.75.0

- [a0d44698](https://github.com/SlimPlanet/SlimFaas/commit/a0d44698461f0431dec86be96af2f6a008b25cfd) - feat(slimdata): batch & benchmark (release), 2026-07-26 by *Guillaume Chervet*


## 0.74.8



## v0.74.8

- [4d836f83](https://github.com/SlimPlanet/SlimFaas/commit/4d836f83c4e3c7df865cb2869c10a85108a6ed43) - fix: memory leak (release) (#300), 2026-07-25 by *Guillaume Chervet*


## 0.74.7



## v0.74.7

- [e9201134](https://github.com/SlimPlanet/SlimFaas/commit/e9201134dc1021726a907d9e887826eb30908c6a) - fix: files ram decrease (#299) (release), 2026-07-24 by *Guillaume Chervet*
- [0c2aaead](https://github.com/SlimPlanet/SlimFaas/commit/0c2aaeadd288ff07896edd2ba80889a3c95b4677) - fix: metrics limit used ram (#298), 2026-07-23 by *Guillaume Chervet*


## 0.74.6



## v0.74.6

- [7679e085](https://github.com/SlimPlanet/SlimFaas/commit/7679e085cc89a3765f2a9f7b87bb50e5d57a655a) - fix: metrics regex timeout (#297) (release), 2026-07-23 by *Guillaume Chervet*


