// Фоновая нагрузка на демо-магазин.
//
// Интенсивность меняется по синусоиде (имитация суточного цикла, сжатого до
// CYCLE_MINUTES), чтобы детектор аномалий учился на «живом» трафике,
// а не на ровной линии. Смесь запросов:
//   55% — карточка товара (горячие товары чаще)
//   15% — поиск по каталогу
//   25% — создание заказа
//    5% — просмотр недавнего заказа
import http from 'k6/http';
import { check } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8000';
const BASE_RPS = Number(__ENV.BASE_RPS || 20);
const CYCLE_MINUTES = Number(__ENV.CYCLE_MINUTES || 60);
const TOTAL_HOURS = Number(__ENV.TOTAL_HOURS || 168);
const PRODUCTS = 2000;
const SEARCH_TERMS = ['Pro', 'Mini', 'Max', 'Eco', 'Classic', 'Product 1', 'Product 42'];

function buildStages() {
  // Шаг — 1/12 цикла; целевая интенсивность от 0.5 до 1.5 базовой.
  const steps = 12;
  const stepSeconds = Math.max(Math.round((CYCLE_MINUTES * 60) / steps), 1);
  const cycles = Math.max(Math.ceil((TOTAL_HOURS * 60) / CYCLE_MINUTES), 1);
  const stages = [];
  for (let c = 0; c < cycles; c++) {
    for (let s = 0; s < steps; s++) {
      const phase = (2 * Math.PI * (s + 1)) / steps;
      const target = Math.round(BASE_RPS * (1 + 0.5 * Math.sin(phase)));
      stages.push({ duration: `${stepSeconds}s`, target: Math.max(target, 1) });
    }
  }
  return stages;
}

export const options = {
  scenarios: {
    shop: {
      executor: 'ramping-arrival-rate',
      startRate: BASE_RPS,
      timeUnit: '1s',
      preAllocatedVUs: 50,
      maxVUs: 400,
      stages: buildStages(),
    },
  },
  // SMOKE=1 — проверка в CI: прогон падает, если ошибок больше 1%.
  thresholds: __ENV.SMOKE ? { http_req_failed: ['rate<0.01'] } : {},
};

// Горячие товары: малые id выпадают заметно чаще.
function productId() {
  return 1 + Math.floor(PRODUCTS * Math.pow(Math.random(), 3));
}

function customerId() {
  return 1 + Math.floor(Math.random() * 5000);
}

const recentOrders = [];

export default function () {
  const r = Math.random();

  if (r < 0.55) {
    const res = http.get(`${BASE_URL}/products/${productId()}`, { tags: { name: 'product' } });
    check(res, { 'product 200': (x) => x.status === 200 });
  } else if (r < 0.7) {
    const term = SEARCH_TERMS[Math.floor(Math.random() * SEARCH_TERMS.length)];
    const res = http.get(`${BASE_URL}/products?search=${encodeURIComponent(term)}&size=20`, {
      tags: { name: 'search' },
    });
    check(res, { 'search 200': (x) => x.status === 200 });
  } else if (r < 0.95) {
    const body = JSON.stringify({
      customerId: customerId(),
      productId: productId(),
      quantity: 1 + Math.floor(Math.random() * 3),
    });
    const res = http.post(`${BASE_URL}/orders`, body, {
      headers: { 'Content-Type': 'application/json' },
      tags: { name: 'create_order' },
    });
    check(res, { 'order 201': (x) => x.status === 201 });
    if (res.status === 201) {
      recentOrders.push(res.json('id'));
      if (recentOrders.length > 100) recentOrders.shift();
    }
  } else if (recentOrders.length > 0) {
    const id = recentOrders[Math.floor(Math.random() * recentOrders.length)];
    const res = http.get(`${BASE_URL}/orders/${id}`, { tags: { name: 'get_order' } });
    check(res, { 'get order 200': (x) => x.status === 200 });
  }
}
