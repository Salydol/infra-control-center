-- Схема демо-магазина. Выполняется один раз при создании тома PostgreSQL.
create table products (
    id       bigint primary key,
    name     text not null,
    category text not null,
    price    numeric(10, 2) not null,
    stock    integer not null
);

create table orders (
    id          bigserial primary key,
    customer_id bigint not null,
    product_id  bigint not null references products (id),
    quantity    integer not null,
    amount      numeric(12, 2) not null,
    status      text not null,
    created_at  timestamptz not null default now(),
    paid_at     timestamptz
);
create index orders_created_at_idx on orders (created_at);

create table payments (
    id         bigserial primary key,
    order_id   bigint not null references orders (id),
    amount     numeric(12, 2) not null,
    status     text not null,
    error      text,
    created_at timestamptz not null default now()
);
create index payments_order_id_idx on payments (order_id);

create table notifications (
    id          bigserial primary key,
    order_id    bigint not null references orders (id),
    customer_id bigint not null,
    channel     text not null,
    sent_at     timestamptz not null default now()
);

insert into products (id, name, category, price, stock)
select i,
       'Product ' || i || ' ' || (array['Classic', 'Pro', 'Mini', 'Max', 'Eco'])[1 + i % 5],
       (array['books', 'electronics', 'home', 'toys', 'sports', 'food'])[1 + i % 6],
       round((5 + random() * 495)::numeric, 2),
       1000
from generate_series(1, 2000) as i;
